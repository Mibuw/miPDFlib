using System.Security.Cryptography.X509Certificates;

namespace MiPdf.Security;

/// <summary>User access permissions (ISO 32000-2, Table 22).</summary>
[Flags]
public enum PdfPermissions
{
    /// <summary>No permissions granted.</summary>
    None = 0,
    /// <summary>Print the document (bit 3); possibly in degraded quality unless <see cref="PrintHighQuality"/> is also set.</summary>
    Print = 1 << 2,
    /// <summary>Modify the contents of the document by operations other than those of the other flags (bit 4).</summary>
    ModifyContents = 1 << 3,
    /// <summary>Copy or otherwise extract text and graphics (bit 5).</summary>
    CopyContents = 1 << 4,
    /// <summary>Add or modify annotations and fill in form fields; with <see cref="ModifyContents"/> also create form fields (bit 6).</summary>
    ModifyAnnotations = 1 << 5,
    /// <summary>Fill in existing form fields, including signature fields, even without <see cref="ModifyAnnotations"/> (bit 9).</summary>
    FillForms = 1 << 8,
    /// <summary>Extract text and graphics for accessibility purposes (bit 10; deprecated in PDF 2.0, readers should always allow this).</summary>
    ExtractForAccessibility = 1 << 9,
    /// <summary>Assemble the document: insert, rotate or delete pages and create bookmarks or thumbnails (bit 11).</summary>
    AssembleDocument = 1 << 10,
    /// <summary>Print in full quality (bit 12); only effective together with <see cref="Print"/>.</summary>
    PrintHighQuality = 1 << 11,
    /// <summary>All of the permissions above.</summary>
    All = Print | ModifyContents | CopyContents | ModifyAnnotations | FillForms | ExtractForAccessibility | AssembleDocument | PrintHighQuality,
}

/// <summary>Encryption algorithm for writing.</summary>
public enum PdfEncryptionAlgorithm
{
    /// <summary>AES with 256-bit key (PDF 2.0, revision 6). Recommended.</summary>
    Aes256,

    /// <summary>AES with 128-bit key (PDF 1.6, revision 4). For older viewers.</summary>
    Aes128,

    /// <summary>RC4 with 128-bit key (PDF 1.4, revision 3). Insecure, only for legacy compatibility.</summary>
    Rc4_128,
}

/// <summary>
/// Settings for encrypting a document: with passwords, or for the certificates of its recipients
/// (<see cref="Recipients"/>).
/// </summary>
public sealed class PdfEncryptionOptions
{
    /// <summary>Password required to open the document (empty = opens without password, restrictions still apply).</summary>
    public string UserPassword { get; set; } = "";

    /// <summary>Password granting full access. If empty, a random password is generated.</summary>
    public string OwnerPassword { get; set; } = "";

    /// <summary>Permissions granted to users who open the document with the user password (default <see cref="PdfPermissions.All"/>).</summary>
    public PdfPermissions Permissions { get; set; } = PdfPermissions.All;

    /// <summary>The encryption algorithm (default <see cref="PdfEncryptionAlgorithm.Aes256"/>).</summary>
    public PdfEncryptionAlgorithm Algorithm { get; set; } = PdfEncryptionAlgorithm.Aes256;

    /// <summary>Encrypt the XMP metadata stream (default true).</summary>
    public bool EncryptMetadata { get; set; } = true;

    /// <summary>
    /// Certificates of the recipients for certificate encryption (public-key security handler, /Adobe.PubSec): only
    /// holders of a matching private key can open the document (<see cref="PdfOpenOptions.Certificate"/>). The
    /// passwords are then not used; <see cref="Permissions"/> apply to all recipients. RSA certificates are supported.
    /// </summary>
    public IList<X509Certificate2> Recipients { get; } = new List<X509Certificate2>();
}
