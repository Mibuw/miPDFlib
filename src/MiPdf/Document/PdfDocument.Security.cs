using System.Security.Cryptography;
using MiPdf.IO;
using MiPdf.Objects;
using MiPdf.Security;

namespace MiPdf;

public sealed partial class PdfDocument
{
    private StandardSecurityHandler? _security;
    private bool _encryptionChanged;

    /// <summary>True if the document is (or will be saved) encrypted.</summary>
    public bool IsEncrypted => Trailer.ContainsKey(PdfName.Encrypt);

    /// <summary>
    /// True if the document was opened with the owner password (or is not encrypted). Without owner
    /// access the <see cref="Permissions"/> should be respected.
    /// </summary>
    public bool IsOwnerAccess => _security?.IsOwnerAccess ?? true;

    /// <summary>
    /// Allows changing or removing the encryption of a document that was opened with the user password only.
    /// The document's permissions are then not enforced by miPDFlib – the caller is responsible for respecting them
    /// (e.g. when the application itself created the document). Default false.
    /// </summary>
    public bool IgnorePermissions { get; set; }

    /// <summary>The permissions granted to users of an encrypted document (all permissions if not encrypted).</summary>
    public PdfPermissions Permissions => _security == null ? PdfPermissions.All : (PdfPermissions)_security.Permissions & PdfPermissions.All;

    /// <summary>Encryption revision of an encrypted document (2–6), 0 if not encrypted.</summary>
    public int EncryptionRevision => _security?.Revision ?? 0;

    private void InitSecurity(string? password)
    {
        if (!IsEncrypted) return;
        var encryptRef = Trailer[PdfName.Encrypt] as PdfReference;
        if (encryptRef != null) Store.EncryptObjectNumber = encryptRef.ObjectNumber;
        var encrypt = Trailer.GetDictionary(PdfName.Encrypt) ?? throw new PdfException("Invalid /Encrypt entry.");
        _security = StandardSecurityHandler.Open(encrypt, Trailer.GetArray(PdfName.ID), password);
        Store.Decryptor = _security;
    }

    /// <summary>
    /// Encrypts the document when it is saved next. Requires a full save (not incremental).
    /// </summary>
    public void Encrypt(PdfEncryptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsOwnerAccess && !IgnorePermissions) throw new PdfException("Changing the encryption requires the owner password (or IgnorePermissions).");
        if (Trailer.GetArray(PdfName.ID) is not { Count: >= 1 } id || id.GetString(0) == null)
        {
            var fresh = new PdfString(RandomNumberGenerator.GetBytes(16), hex: true);
            Trailer[PdfName.ID] = new PdfArray(fresh, fresh);
            id = Trailer.GetArray(PdfName.ID)!;
        }
        var (handler, dict) = StandardSecurityHandler.Create(options, id.GetString(0)!.Bytes);
        Trailer[PdfName.Encrypt] = AddObject(dict);
        _security = handler;
        _encryptionChanged = true;
        if (options.Algorithm == PdfEncryptionAlgorithm.Aes256 && string.CompareOrdinal(Version, "2.0") < 0) Version = "2.0";
        else if (options.Algorithm == PdfEncryptionAlgorithm.Aes128 && string.CompareOrdinal(Version, "1.6") < 0) Version = "1.6";
    }

    /// <summary>Removes the encryption; the document is saved unencrypted. Requires owner access.</summary>
    public void RemoveEncryption()
    {
        if (!IsEncrypted) return;
        if (!IsOwnerAccess && !IgnorePermissions) throw new PdfException("Removing the encryption requires the owner password (or IgnorePermissions).");
        Trailer.Remove(PdfName.Encrypt);
        _security = null;
        _encryptionChanged = true;
    }

    private IObjectEncryptor? CreateEncryptor(PdfSaveOptions options)
    {
        if (options.Incremental && _encryptionChanged)
            throw new PdfException("Encryption settings were changed; the document must be saved completely (Incremental = false).");
        return IsEncrypted ? _security : null;
    }
}
