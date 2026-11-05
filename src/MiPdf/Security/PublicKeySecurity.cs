using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using MiPdf.Objects;
using Cipher = MiPdf.Security.StandardSecurityHandler.Cipher;

namespace MiPdf.Security;

/// <summary>
/// Public-key security handler (ISO 32000-2, 7.6.5; /Filter /Adobe.PubSec): the document is encrypted for the
/// certificates of its recipients. Each entry of /Recipients is a CMS EnvelopedData that contains a 20-byte seed and
/// the 4-byte permissions for its recipients; the file key is the SHA-1 (AES-256: SHA-256) hash of the seed and all
/// /Recipients entries. Objects are then encrypted as with the standard security handler.
/// </summary>
internal static class PublicKeySecurity
{
    private static readonly PdfName DefaultCryptFilter = PdfName.Get("DefaultCryptFilter");
    private static readonly PdfName Recipients = PdfName.Get("Recipients");
    private const string Aes256CbcOid = "2.16.840.1.101.3.4.1.42";

    public static bool IsPublicKey(PdfDictionary encrypt) => encrypt.GetName(PdfName.Filter)?.Value == "Adobe.PubSec";

    public static StandardSecurityHandler Open(PdfDictionary encrypt, X509Certificate2? certificate)
    {
        if (certificate == null || !certificate.HasPrivateKey)
            throw new PdfPasswordException("The document is encrypted for certificates: open it with a certificate and its private key (PdfOpenOptions.Certificate).");
        int v = encrypt.GetInt(PdfName.V);
        PdfObject? recipients;
        bool encryptMetadata = true;
        Cipher streamCipher, stringCipher;
        int length;
        if (v >= 4)
        {
            string filter = encrypt.GetName(PdfName.StmF)?.Value ?? "Identity";
            var cryptFilter = encrypt.GetDictionary(PdfName.CF)?.GetDictionary(PdfName.Get(filter))
                ?? throw new PdfException($"Encryption dictionary without crypt filter '{filter}'.");
            recipients = cryptFilter.Get(Recipients);
            encryptMetadata = cryptFilter.GetBool(PdfName.EncryptMetadata, true);
            streamCipher = StandardSecurityHandler.CipherFromFilter(encrypt, filter);
            stringCipher = StandardSecurityHandler.CipherFromFilter(encrypt, encrypt.GetName(PdfName.StrF)?.Value ?? "Identity");
            int bits = cryptFilter.GetInt(PdfName.Length, 128);
            // /Length of crypt filters is given in bits or (by some writers) in bytes
            length = streamCipher switch
            {
                Cipher.Aes256 => 32,
                Cipher.Aes128 => 16,
                _ => Math.Clamp(bits > 32 ? bits / 8 : bits, 5, 16),
            };
        }
        else
        {
            recipients = encrypt.Get(Recipients);
            streamCipher = stringCipher = Cipher.Rc4;
            length = Math.Clamp(encrypt.GetInt(PdfName.Length, 40) / 8, 5, 16);
        }

        var entries = (recipients as PdfArray ?? (recipients != null ? new PdfArray(recipients) : new PdfArray()))
            .Select(r => (r.Resolve() as PdfString)?.Bytes).OfType<byte[]>().ToList();
        if (entries.Count == 0) throw new PdfException("Encryption dictionary without /Recipients.");

        byte[]? content = null;
        foreach (var entry in entries)
        {
            content = TryDecrypt(entry, certificate);
            if (content != null) break;
        }
        if (content == null || content.Length < 24)
            throw new PdfPasswordException("The document is not encrypted for this certificate.");

        int permissions = BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(20, 4));
        var key = DeriveKey(content.AsSpan(0, 20), entries, encryptMetadata, streamCipher == Cipher.Aes256, length);
        bool ownerAccess = ((PdfPermissions)permissions & PdfPermissions.All) == PdfPermissions.All;
        return new StandardSecurityHandler(key, streamCipher, stringCipher, encryptMetadata, v, permissions, ownerAccess);
    }

    /// <summary>Decrypts one /Recipients entry with the certificate's private key, or returns null if it is not addressed to it.</summary>
    private static byte[]? TryDecrypt(byte[] entry, X509Certificate2 certificate)
    {
        try
        {
            var cms = new EnvelopedCms();
            cms.Decode(entry);
            foreach (var recipient in cms.RecipientInfos)
            {
                if (!Matches(recipient.RecipientIdentifier, certificate)) continue;
                using AsymmetricAlgorithm? key = certificate.GetRSAPrivateKey() ?? (AsymmetricAlgorithm?)certificate.GetECDiffieHellmanPrivateKey();
                if (key == null) return null;
                cms.Decrypt(recipient, key);
                return cms.ContentInfo.Content;
            }
        }
        catch (CryptographicException)
        {
            // not readable or not for this certificate
        }
        return null;
    }

    private static bool Matches(SubjectIdentifier identifier, X509Certificate2 certificate) =>
        identifier.Value switch
        {
            System.Security.Cryptography.Xml.X509IssuerSerial issuerSerial => string.Equals(issuerSerial.SerialNumber, certificate.SerialNumber, StringComparison.OrdinalIgnoreCase)
                && issuerSerial.IssuerName == certificate.IssuerName.Name,
            string keyIdentifier => certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>()
                .Any(e => string.Equals(e.SubjectKeyIdentifier, keyIdentifier, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };

    private static byte[] DeriveKey(ReadOnlySpan<byte> seed, IEnumerable<byte[]> recipients, bool encryptMetadata, bool sha256, int length)
    {
        using var hash = IncrementalHash.CreateHash(sha256 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
        hash.AppendData(seed);
        foreach (var recipient in recipients) hash.AppendData(recipient);
        if (!encryptMetadata) hash.AppendData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        return hash.GetHashAndReset().AsSpan(0, length).ToArray();
    }

    /// <summary>Creates a handler for encryption for <see cref="PdfEncryptionOptions.Recipients"/> and the /Encrypt dictionary.</summary>
    public static (StandardSecurityHandler Handler, PdfDictionary Dictionary) Create(PdfEncryptionOptions options)
    {
        int p = unchecked((int)0xFFFFF0C0) | (int)options.Permissions;
        var seed = RandomNumberGenerator.GetBytes(20);
        var content = new byte[24];
        seed.CopyTo(content, 0);
        BinaryPrimitives.WriteInt32BigEndian(content.AsSpan(20), p);

        var recipients = new CmsRecipientCollection();
        foreach (var certificate in options.Recipients)
            recipients.Add(new CmsRecipient(SubjectIdentifierType.IssuerAndSerialNumber, certificate));
        var cms = new EnvelopedCms(new ContentInfo(content), new AlgorithmIdentifier(new Oid(Aes256CbcOid)));
        cms.Encrypt(recipients);
        var entry = cms.Encode();

        var (cipher, cfm, v, length) = options.Algorithm switch
        {
            PdfEncryptionAlgorithm.Aes256 => (Cipher.Aes256, "AESV3", 5, 32),
            PdfEncryptionAlgorithm.Aes128 => (Cipher.Aes128, "AESV2", 4, 16),
            _ => (Cipher.Rc4, "V2", 4, 16),
        };
        var key = DeriveKey(seed, new[] { entry }, options.EncryptMetadata, cipher == Cipher.Aes256, length);

        var cryptFilter = new PdfDictionary();
        cryptFilter[PdfName.CFM] = PdfName.Get(cfm);
        cryptFilter[PdfName.Get("AuthEvent")] = PdfName.Get("DocOpen");
        cryptFilter.Set(PdfName.Length, length * 8);
        cryptFilter[Recipients] = new PdfArray(new PdfString(entry, hex: true));
        if (!options.EncryptMetadata) cryptFilter.Set(PdfName.EncryptMetadata, false);
        var dict = new PdfDictionary();
        dict[PdfName.Filter] = PdfName.Get("Adobe.PubSec");
        dict[PdfName.SubFilter] = PdfName.Get("adbe.pkcs7.s5");
        dict.Set(PdfName.V, v);
        dict.Set(PdfName.Length, length * 8);
        dict[PdfName.CF] = new PdfDictionary { [DefaultCryptFilter] = cryptFilter };
        dict[PdfName.StmF] = DefaultCryptFilter;
        dict[PdfName.StrF] = DefaultCryptFilter;
        if (!options.EncryptMetadata) dict.Set(PdfName.EncryptMetadata, false);
        return (new StandardSecurityHandler(key, cipher, cipher, options.EncryptMetadata, v, p, ownerAccess: true), dict);
    }
}
