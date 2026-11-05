# Encryption and permissions

## Opening protected documents

```csharp
using var doc = PdfDocument.Open("protected.pdf", password: "secret");  // user or owner password
Console.WriteLine($"encrypted: {doc.IsEncrypted}, revision {doc.EncryptionRevision}");
Console.WriteLine($"owner access: {doc.IsOwnerAccess}, permissions: {doc.Permissions}");
```

`PdfPasswordException` is thrown if a password is required or wrong. Documents with an empty user
password open without a password; their permissions are then reported via `Permissions`
(`IsOwnerAccess == false`). Applications should respect them.

Supported for reading: standard security handler revisions 2–6 (RC4 40/128 bit, AES-128, AES-256 incl.
the Acrobat 9 variant R5), crypt filters (`/StmF`, `/StrF`, `/Identity`), unencrypted metadata, and documents
encrypted for certificates (see [below](#certificate-encryption)).

## Encrypting

```csharp
doc.Encrypt(new PdfEncryptionOptions
{
    UserPassword = "open-me",         // empty = no password needed to open
    OwnerPassword = "full-access",    // empty = random password
    Permissions = PdfPermissions.Print | PdfPermissions.CopyContents,
    Algorithm = PdfEncryptionAlgorithm.Aes256,   // default; Aes128 / Rc4_128 for old viewers
});
doc.Save("protected.pdf");
```

AES-256 raises the document version to 2.0, AES-128 to 1.6.

| Permission | Meaning |
|---|---|
| `Print` / `PrintHighQuality` | print (low / full quality) |
| `ModifyContents` | change the document |
| `CopyContents` | copy text and graphics |
| `ModifyAnnotations` | add/modify comments and fill forms |
| `FillForms` | fill form fields |
| `ExtractForAccessibility` | text extraction for accessibility |
| `AssembleDocument` | insert, rotate, delete pages, bookmarks |

## Certificate encryption

Instead of passwords, a document can be encrypted for the certificates of its recipients (public-key security
handler, `/Adobe.PubSec`): only someone with the private key of one of the certificates can open it – no password
has to be passed on.

```csharp
var options = new PdfEncryptionOptions { Permissions = PdfPermissions.Print | PdfPermissions.CopyContents };
options.Recipients.Add(X509CertificateLoader.LoadCertificateFromFile("alice.cer"));   // public certificates
options.Recipients.Add(X509CertificateLoader.LoadCertificateFromFile("bob.cer"));
doc.Encrypt(options);
doc.Save("for-alice-and-bob.pdf");

using var certificate = X509CertificateLoader.LoadPkcs12FromFile("alice.pfx", "pfx-password");  // with private key
// .NET 8: new X509Certificate2("alice.cer") and new X509Certificate2("alice.pfx", "pfx-password")
using var opened = PdfDocument.Open("for-alice-and-bob.pdf", new PdfOpenOptions { Certificate = certificate });
Console.WriteLine($"{opened.IsCertificateEncrypted}, permissions: {opened.Permissions}");
```

- The algorithms are the same as for passwords (`Aes256` default, `Aes128`, `Rc4_128`); the seed and the permissions
  are encrypted for the recipients as CMS EnvelopedData with AES-256 and RSA key transport (`/SubFilter
  /adbe.pkcs7.s5`). RSA certificates are supported, also from the Windows certificate store or a smart card when
  the private key is accessible to .NET.
- All recipients get the same `Permissions`; a recipient whose permissions include all permissions has owner
  access (`IsOwnerAccess`). Passwords and recipients cannot be combined.
- Opening without a certificate, or with a certificate the document is not encrypted for, throws
  `PdfPasswordException`. `PdfOpenOptions` with a certificate can be passed to `Open` for files, byte arrays and
  streams.
- Signatures of a document encrypted for certificates are validated with `PdfSignatureValidator.Validate(document)`
  on the opened document.

## Changing or removing encryption

```csharp
using var doc = PdfDocument.Open("protected.pdf", "full-access");   // owner password required
doc.RemoveEncryption();             // or doc.Encrypt(new options)
doc.Save("unprotected.pdf");        // must be a full save (not incremental)
```

Saving an encrypted document without changing the encryption keeps it encrypted with the same key –
also for incremental updates (e.g. adding a signature).

## Documents opened with the user password

Changing or removing the encryption requires the owner password. If your application created the document itself
(or is otherwise entitled to change it), set `doc.IgnorePermissions = true` – miPDFlib then allows `Encrypt` and
`RemoveEncryption` with user access. The caller is responsible for respecting the document's permissions.
