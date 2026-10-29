namespace MiPdf.Security;

/// <summary>User access permissions (ISO 32000-2, Table 22).</summary>
[Flags]
public enum PdfPermissions
{
    None = 0,
    Print = 1 << 2,
    ModifyContents = 1 << 3,
    CopyContents = 1 << 4,
    ModifyAnnotations = 1 << 5,
    FillForms = 1 << 8,
    ExtractForAccessibility = 1 << 9,
    AssembleDocument = 1 << 10,
    PrintHighQuality = 1 << 11,
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

/// <summary>Settings for encrypting a document with passwords.</summary>
public sealed class PdfEncryptionOptions
{
    /// <summary>Password required to open the document (empty = opens without password, restrictions still apply).</summary>
    public string UserPassword { get; set; } = "";

    /// <summary>Password granting full access. If empty, a random password is generated.</summary>
    public string OwnerPassword { get; set; } = "";

    public PdfPermissions Permissions { get; set; } = PdfPermissions.All;

    public PdfEncryptionAlgorithm Algorithm { get; set; } = PdfEncryptionAlgorithm.Aes256;

    /// <summary>Encrypt the XMP metadata stream (default true).</summary>
    public bool EncryptMetadata { get; set; } = true;
}
