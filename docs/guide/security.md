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
the Acrobat 9 variant R5), crypt filters (`/StmF`, `/StrF`, `/Identity`), unencrypted metadata.
Not supported: public-key (certificate) security handlers.

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
