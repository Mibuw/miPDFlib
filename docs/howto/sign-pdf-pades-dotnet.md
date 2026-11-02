# How to sign a PDF with PAdES in .NET (C#)

This how-to signs a PDF document in C# with an advanced electronic signature in the PAdES format (ETSI EN 319 142),
the format required for eIDAS signatures in PDF. It covers the four PAdES baseline levels: B-B, B-T (time-stamp),
B-LT (validation data) and B-LTA (archive time-stamp). The library is
[miPDFlib](https://github.com/Mibuw/miPDFlib) (Apache-2.0); the signature is built with the CMS support in .NET,
no BouncyCastle is needed.

```bash
dotnet add package miPDFlib
```

## 1. Sign with a certificate (PAdES B-B)

```csharp
using MiPdf;
using MiPdf.Signatures;

var signer = CertificateSigner.FromPfx("signer.pfx", "password");   // private key + chain from a PKCS#12 file
byte[] signed = PdfSigner.Sign(File.ReadAllBytes("contract.pdf"), signer, new PdfSignatureOptions
{
    Reason = "Approved",
    Location = "Vienna",
});
File.WriteAllBytes("contract-signed.pdf", signed);
```

The signature is appended as an incremental update, so existing signatures in the document stay valid. The default
format is PAdES (`ETSI.CAdES.detached` with the signing-certificate-v2 attribute) with SHA-256. RSA (PKCS#1 v1.5
or RSASSA-PSS) and ECDSA keys (NIST and Brainpool curves) are supported.

A certificate from the Windows certificate store or a smart card works the same way: pass an `X509Certificate2`
with private key instead of the `CertificateSigner`.

## 2. Visible signature

```csharp
var options = new PdfSignatureOptions
{
    PageNumber = 1,
    Rectangle = new PdfRectangle(350, 50, 550, 110),                  // points, origin bottom left
    Appearance = new PdfSignatureAppearance
    {
        Text = "Signed by {name}\n{date}\nReason: {reason}",
        Image = MiPdf.Images.PdfImage.FromFile("signature.png"),      // optional: handwritten signature image
    },
};
```

On rotated pages the appearance is drawn upright automatically. Without `Rectangle` the signature is invisible.

## 3. Add a time-stamp (PAdES B-T)

A time-stamp from a time-stamp authority (RFC 3161) proves when the document was signed. Several authorities can be
combined; the next one is tried if one fails:

```csharp
var tsa = FailoverTimestampClient.FromUrls(new[]
{
    "http://timestamp.digicert.com",
    "http://timestamp.sectigo.com",
});
byte[] signed = PdfSigner.Sign(pdf, signer, new PdfSignatureOptions { TimestampClient = tsa });
```

For qualified time-stamps use the authority of your trust service provider.

## 4. Long-term validation (PAdES B-LT and B-LTA)

A signature has to remain verifiable after the signer certificate has expired or the CA has stopped answering
revocation requests. B-LT embeds the certificates, OCSP responses and CRLs in the document (Document Security
Store); B-LTA adds a document time-stamp over everything:

```csharp
LtvResult lta = PdfLtv.MakeLta(signed, tsa);                      // validation data + document time-stamp
if (!lta.IsComplete)
    Console.WriteLine(string.Join(Environment.NewLine, lta.Warnings));
File.WriteAllBytes("contract-lta.pdf", lta.Pdf);                  // lta.Level == PadesLevel.BLTA
```

`PdfLtv.AddValidationData(signed)` stops at B-LT. Both also work for signatures created by other software, and
`MakeLta` can be called again before the last time-stamp expires to extend the archive period.

## 5. Keys in an HSM, smart card or cloud signing service

If the private key cannot be exported, implement `IExternalSigner`. Only the hash is passed to it:

```csharp
public sealed class HsmSigner : IExternalSigner
{
    public X509Certificate2 Certificate { get; init; } = null!;                       // signer certificate
    public IReadOnlyList<X509Certificate2> CertificateChain { get; init; } = [];       // intermediates to embed
    public byte[] SignHash(byte[] hash, HashAlgorithmName algorithm) => hsm.Sign(hash, algorithm);
}
```

For services that sign in a separate step (remote signing, mobile signatures), split signing in two:
`PdfSigner.PrepareExternalSignature` returns the digest to sign, and `PdfSigner.CompleteExternalSignature` embeds
the CMS container returned by the service. See [Signatures](../guide/signatures.md#deferred-two-step-signing-with-a-signing-service).

## 6. Verify the signature

```csharp
foreach (var result in PdfSignatureValidator.Validate(File.ReadAllBytes("contract-lta.pdf")))
    Console.WriteLine($"{result.FieldName}: integrity {result.IsIntegrityValid}, level {result.Level}, " +
                      $"whole document {result.CoversWholeDocument}, time-stamp {result.TimestampTime}");
```

For a full assessment against the EU trusted lists use a validation service based on the EU DSS library, for example
[miPDFvalidator](https://github.com/Mibuw/miPDFvalidator).

## More

- Certification (DocMDP) and field locks (FieldMDP), custom signed attributes, deferred signing with a raw
  signature value: [Signatures guide](../guide/signatures.md)
- Coming from iText or Syncfusion: [comparison](../COMPARISON.md) and [migration guide](../MIGRATION.md)
- Background on the levels: [PAdES B-T, LT and LTA explained](https://www.mitterbucher.com/en/articles/pades-b-t-lt-lta/)
