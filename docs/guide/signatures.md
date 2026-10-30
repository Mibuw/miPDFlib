# Digital signatures

Signatures are always added as an **incremental update**: the original bytes stay untouched, so existing
signatures remain valid. The CMS container is created with `System.Security.Cryptography.Pkcs` (Microsoft).

## Signing with a certificate file

```csharp
using MiPdf.Signatures;

var signer = CertificateSigner.FromPfx("signer.pfx", "password");   // key + chain from PKCS#12
byte[] signed = PdfSigner.Sign(File.ReadAllBytes("contract.pdf"), signer, new PdfSignatureOptions
{
    Reason = "Approval",
    Location = "Vienna",
    ContactInfo = "office@example.com",
    // visible signature on page 1; omit Rectangle for an invisible signature
    PageNumber = 1,
    Rectangle = new PdfRectangle(350, 50, 550, 110),
    // RFC 3161 signature time-stamp -> PAdES B-T
    TimestampClient = new HttpTimestampClient("http://timestamp.digicert.com"),
});
File.WriteAllBytes("contract-signed.pdf", signed);
```

Overloads accept an `X509Certificate2` with private key (from the Windows certificate store, a smart card
CSP/KSP, ...) or a `PdfDocument`.

| Option | Description |
|---|---|
| `Format` | `PAdES` (default, `ETSI.CAdES.detached`, with signing-certificate-v2) or `Pkcs7Detached` (`adbe.pkcs7.detached`, with signing-time and signing-certificate-v2) |
| `DigestAlgorithm` | SHA-256 (default), SHA-384, SHA-512 |
| `RsaPadding` | RSA keys: `RSASignaturePadding.Pkcs1` (default, PKCS#1 v1.5) or `RSASignaturePadding.Pss` (RSASSA-PSS, MGF1 with the digest algorithm, salt length = digest length) |

### Key types

| Key | Supported |
|---|---|
| RSA | PKCS#1 v1.5 and RSASSA-PSS, any key size |
| ECDSA | NIST P-256, P-384, P-521 and Brainpool P256r1, P384r1, P512r1 (tested); any other curve the platform supports. Use a digest that matches the curve size (e.g. P-384 with SHA-384). |

Certificates issued with RSASSA-PSS are supported for chain building, LTV and validation as well.
| `FieldName` | existing empty signature field to sign, or name of the new field (default `Signature1`, `Signature2`, ...) |
| `TimestampClient` | time-stamp authority (B-T level) |
| `Certification` | certify the document (DocMDP): `NoChanges`, `FormFilling`, `FormFillingAndAnnotations` |
| `FieldLock` | form fields locked by this signature (FieldMDP): `PdfFieldLock.All()`, `Include(..)`, `Exclude(..)` |
| `Appearance` | text template (`{name}`, `{date}`, `{reason}`, `{location}`, `{contact}`), image (left, background or image only), font, colors |
| `Password` | password of an encrypted document |
| `SignatureSize` | reserved container size (default: estimated automatically) |

## HSM, smart cards, cloud signing – `IExternalSigner`

When the private key cannot be exported, implement `IExternalSigner`; only the hash is passed to it:

```csharp
public sealed class MyHsmSigner : IExternalSigner
{
    public X509Certificate2 Certificate { get; }                  // signer certificate (public)
    public IReadOnlyList<X509Certificate2> CertificateChain { get; } // intermediates to embed
    public byte[] SignHash(byte[] hash, HashAlgorithmName alg)
        => hsm.Sign(hash, alg);  // RSA: PKCS#1 v1.5 signature; ECDSA: IEEE P1363 (r||s)

    // optional, only needed for RsaPadding = RSASignaturePadding.Pss
    public byte[] SignHash(byte[] hash, HashAlgorithmName alg, RSASignaturePadding padding)
        => hsm.Sign(hash, alg, padding);
}
byte[] signed = PdfSigner.Sign(pdf, new MyHsmSigner(...), options);
```

Signers that do not implement the padding overload are rejected with `NotSupportedException` when PSS is
requested; they are not asked to sign.

## Deferred (two-step) signing with a signing service

For services that return a complete CMS/PKCS#7 container (e.g. remote/mobile signature services):

```csharp
// step 1: prepare (store PreparedPdf if step 2 runs later or in another process)
var prepared = PdfSigner.PrepareExternalSignature(pdf, new PdfSignatureOptions { FieldName = "sig1" }, certificate);
byte[] digest = prepared.GetDocumentDigest();      // SHA-256 of the signed byte ranges
// ... send digest to the service, receive a detached CMS signature container ...
byte[] signed = PdfSigner.CompleteExternalSignature(prepared, cmsFromService);
// or later: PdfSigner.CompleteExternalSignature(storedPreparedPdf, "sig1", cmsFromService);
```

### Deferred signing with a raw signature value

For services that only sign a hash and return the raw signature value (possibly in a later HTTP round trip),
build the signed attributes yourself and assemble the CMS afterwards:

```csharp
var prepared = PdfSigner.PrepareExternalSignature(pdf, options, signerCertificate, containerSize: 65536);
byte[] attributes = CmsUtil.CreateSignedAttributes(prepared.GetDocumentDigest(), signerCertificate,
    additionalAttributes: biometricAttributes);          // store PreparedPdf + attributes between the steps
byte[] hashToSign = SHA256.HashData(attributes);
// ... later: signature value from the service (RSA PKCS#1 v1.5 or PSS, or ECDSA r||s / DER)
byte[] cms = CmsUtil.AssembleSignedData(attributes, signatureValue, signerCertificate, chain);
// RSASSA-PSS value: CmsUtil.AssembleSignedData(..., rsaPadding: RSASignaturePadding.Pss)
cms = CmsUtil.AddSignatureTimestamp(cms, tsa);           // optional: B-T
byte[] signed = PdfSigner.CompleteExternalSignature(prepared, cms);
```

`CmsUtil.AddSignatureTimestamp` also works for containers created by other software (remote signing services,
smart card middleware, ...): the signed parts are copied byte by byte. `CmsUtil.GetSignedAttribute`/`GetUnsignedAttribute`
read attributes of any container.

## Custom signed attributes (e.g. biometric data)

```csharp
var w = new AsnWriter(AsnEncodingRules.DER);
w.WriteOctetString(encryptedBiometricBlob);
// use an OID from your own arc; 1.3.6.1.4.1.32473 is the example enterprise number of RFC 5612
options.AdditionalSignedAttributes.Add(new AsnEncodedData("1.3.6.1.4.1.32473.1.1", w.Encode()));
// reading: PdfSignatureValidator.Validate(pdf)[0].GetSignedAttribute("1.3.6.1.4.1.32473.1.1")
```

The reserved signature size is enlarged automatically.

## Custom entries in the signature dictionary

Some applications store data directly in the signature dictionary (inside the signed byte range):

```csharp
options.SignatureDictionaryEntries["Biometrics"] = new PdfString(encryptedBase64);
options.SignatureDictionaryEntries["MyAppData"] = new PdfString("100.5;200");
options.SignatureCreator = "MyApp";                        // /Prop_Build /App /Name
// reading
string? bio = PdfSignatureValidator.Validate(pdf)[0].GetSignatureDictionaryText("Biometrics");
```

## Field locks (FieldMDP)

A signature can lock form fields: the lock (/Lock on the signature field) and a FieldMDP reference in the signature
dictionary are covered by the signature, so a later change of a locked field makes the signature invalid in Adobe
Acrobat instead of being shown as permitted form filling. Without a lock, approval signatures only protect the
signed revision, and filling any field afterwards is an allowed change.

```csharp
// lock the fields of this signer when signing
var options = new PdfSignatureOptions { FieldName = "Employee", FieldLock = PdfFieldLock.Include("name", "date") };
byte[] signed = PdfSigner.Sign(pdf, signer, options);

// or let the form author define the lock in advance: whoever signs the field applies it
form.AddSignatureField(page, "Manager", rect).Lock = PdfFieldLock.All();

// validation: locked fields changed after signing
var result = PdfSignatureValidator.Validate(signed)[0];
Console.WriteLine($"{result.FieldLock}: changed {string.Join(", ", result.ModifiedLockedFields)}");
```

Field names are fully qualified (`"address.street"`); unknown names are rejected when signing. `FieldLock` can be
combined with `Certification` (both references are written, DocMDP first) and works with deferred signing.
`PdfFieldLock.All()` and `Exclude(..)` also lock fields that are added later.

## Time-stamp failover and optional time-stamps

```csharp
var tsa = FailoverTimestampClient.FromUrls(new[] { "http://tsa.belgium.be/connect", "http://timestamp.digicert.com" });
var result = PdfSigner.SignDetailed(pdf, signer, new PdfSignatureOptions { TimestampClient = tsa, TimestampOptional = true });
Console.WriteLine($"{result.Level} {string.Join("; ", result.Warnings)}");   // BT, or BB if all TSAs failed
```

## Handwritten signatures and custom appearances

```csharp
options.Appearance = new PdfSignatureAppearance
{
    Text = "",                                            // no text
    Draw = (canvas, width, height) => InkRenderer.Draw(canvas, strokes,  // pressure-sensitive vector ink
        minWidth: 0.35, maxWidth: 2.4, topLeftHeight: height),           // screen coordinates (top-left origin)
};
// image above a text strip:
options.Appearance = new PdfSignatureAppearance { Image = png, ImagePosition = SignatureImagePosition.Top, Text = "Signed by {name}\n{date}" };
```

On rotated pages (`/Rotate`) the appearance is drawn upright as seen in viewers (`KeepUpright`, default true); an
existing signature field with `/MK /R` is drawn with that rotation. `Rectangle` stays in unrotated page coordinates,
so on a page rotated by 90 or 270 degrees the visible width of the signature is the rectangle's height. The `Draw`
callback receives the upright width and height.

## Long-term validation: PAdES B-LT and B-LTA

```csharp
var lt = PdfLtv.AddValidationData(signedPdf);                    // B-LT: certificates, OCSP responses, CRLs in /DSS
var lta = PdfLtv.MakeLta(signedPdf, tsa);                        // B-LTA: + document time-stamp (+ its validation data)
Console.WriteLine($"{lta.Level} complete={lta.IsComplete} {string.Join("; ", lta.Warnings)}");
```

- Works for all signatures in the document, including signatures created by other software (`FieldNames` restricts it).
- Missing issuer certificates are downloaded via AIA (caIssuers); OCSP is preferred, CRLs are the fallback
  (`IncludeCrlsWithOcsp`, `UseOcsp`, `UseCrls`). Delegated OCSP responder certificates are embedded as well.
- `HttpRevocationSource` caches CRLs and issuers (reuse the instance), limits CRL size (`MaxCrlSize`, default 5 MB).
  Implement `IRevocationSource` for custom caching, proxies or offline data.
- Failures never break the document: they are listed in `Warnings`, `IsComplete` tells whether every certificate is covered.
- `/VRI` entries are optional (`IncludeVri`). The PAdES extension (`/Extensions /ESIC`) is declared.
- Verified against DigiCert (OCSP responses checked with `openssl ocsp`, CRLs with `openssl crl`).

## Document time-stamps

```csharp
byte[] stamped = PdfSigner.AddDocumentTimestamp(signedPdf, new HttpTimestampClient("http://timestamp.digicert.com"));
```

## Verifying signatures

```csharp
var options = new SignatureValidationOptions { CheckCertificateChain = true };   // optional
foreach (var r in PdfSignatureValidator.Validate(File.ReadAllBytes("signed.pdf"), options))
{
    Console.WriteLine($"{r.FieldName}: valid={r.IsIntegrityValid}, signer={r.SignerName}, " +
                      $"whole document={r.CoversWholeDocument}, timestamp={r.TimestampTime}, trusted={r.IsCertificateTrusted}");
}
```

| Property | Meaning |
|---|---|
| `IsIntegrityValid` | signed bytes unchanged, message digest and signature value correct, byte range excludes exactly the container |
| `CoversWholeDocument` | no incremental updates after this signature; otherwise `GetSignedRevision` returns the signed version |
| `TimestampTime` | verified signature time-stamp or document time-stamp |
| `IsCertificateTrusted` | chain built against the system store or `TrustedRoots` (when `CheckCertificateChain` is set) |
| `CertificationLevel` | DocMDP level of a certification signature |
| `FieldLock`, `ModifiedLockedFields` | FieldMDP lock of the signature and the locked fields whose value was changed or that were removed after signing |
| `Level` | PAdES level (B-B, B-T, B-LT, B-LTA) derived from time-stamps, DSS and later document time-stamps |
| `Cms`, `Certificates`, `GetSignedAttribute(oid)` | raw container, embedded certificates, attribute values (e.g. biometric data) |

Apart from the values of locked fields (`ModifiedLockedFields`), whether changes after a signature are *permitted*
(DocMDP analysis, other object changes) is not evaluated; the validator reports that later revisions exist.

## Standards and verification

- PAdES baseline B-B / B-T (ETSI EN 319 142-1): `ETSI.CAdES.detached`, signing-certificate-v2,
  no signing-time attribute, claimed time in `/M`, signature time-stamp as unsigned attribute.
- Verified independently with OpenSSL (`openssl cms -verify` over the extracted byte ranges).
- Validated with EU DSS: RSA PKCS#1 v1.5 and RSASSA-PSS (SHA-256/512, also deferred and with
  a PSS-issued certificate), ECDSA P-256/P-384/P-521 and Brainpool P256r1/P384r1/P512r1 (incl. raw deferred
  signature values), multiple
  signatures with certification, PKCS#7, ink appearances are cryptographically intact and valid; DSS classifies
  the output as PAdES-BASELINE-B / -T / -LT / -LTA and accepts the embedded OCSP responses and CRLs. For B-LT,
  every certificate of the chain needs revocation data – private CAs must publish a CRL/OCSP or provide it via
  a custom `IRevocationSource`.
- PAdES B-LT / B-LTA: DSS with certificates, OCSP responses and CRLs, document time-stamps.
- Field locks: EU DSS reads the /SigFieldLock and FieldMDP dictionaries (action and field list) and accepts them; Adobe
  Acrobat keeps the signature valid when an unlocked field is filled and reports it as invalid when a locked field is changed.
- Not yet supported: /P in field locks (PDF 2.0), legacy `adbe.x509.rsa_sha1` creation, full evaluation of permitted
  changes after signing (DocMDP), revocation checking of the stored validation data in the validator.
