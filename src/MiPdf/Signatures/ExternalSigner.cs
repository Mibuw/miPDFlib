using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MiPdf.Signatures;

/// <summary>
/// A signer whose private key is not available as <see cref="X509Certificate2"/> private key, e.g. an HSM,
/// a smart card middleware, a cloud signing service or a remote key vault. Only the hash is sent to the signer.
/// </summary>
public interface IExternalSigner
{
    /// <summary>The signer certificate (public part).</summary>
    X509Certificate2 Certificate { get; }

    /// <summary>Intermediate certificates to embed (may be empty).</summary>
    IReadOnlyList<X509Certificate2> CertificateChain { get; }

    /// <summary>
    /// Signs a hash. RSA keys must return a PKCS#1 v1.5 signature; EC keys must return the signature in
    /// IEEE P1363 format (r || s).
    /// </summary>
    byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm);

    /// <summary>
    /// Signs a hash with an RSA key and the given padding (PKCS#1 v1.5 or PSS with a salt as long as the hash).
    /// The default implementation supports PKCS#1 v1.5 only; implement this method to support RSASSA-PSS.
    /// </summary>
    byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) =>
        padding == RSASignaturePadding.Pkcs1
            ? SignHash(hash, hashAlgorithm)
            : throw new NotSupportedException($"{GetType().Name} does not support RSASSA-PSS; implement SignHash(hash, hashAlgorithm, padding).");
}

/// <summary>An <see cref="IExternalSigner"/> backed by a local certificate with private key.</summary>
public sealed class CertificateSigner : IExternalSigner
{
    public CertificateSigner(X509Certificate2 certificate, IEnumerable<X509Certificate2>? chain = null)
    {
        if (!certificate.HasPrivateKey) throw new ArgumentException("The certificate has no private key.", nameof(certificate));
        Certificate = certificate;
        CertificateChain = chain?.ToList() ?? new List<X509Certificate2>();
    }

    /// <summary>Loads a PKCS#12 (.pfx/.p12) file and uses the first certificate with a private key; the other certificates form the chain.</summary>
    public static CertificateSigner FromPfx(string path, string? password)
    {
#if NET9_0_OR_GREATER
        var collection = X509CertificateLoader.LoadPkcs12CollectionFromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
#else
        var collection = new X509Certificate2Collection();
        collection.Import(path, password, X509KeyStorageFlags.EphemeralKeySet);
#endif
        var signer = collection.FirstOrDefault(c => c.HasPrivateKey) ?? throw new PdfException("The PKCS#12 file contains no private key.");
        return new CertificateSigner(signer, collection.Where(c => !ReferenceEquals(c, signer)));
    }

    public X509Certificate2 Certificate { get; }

    public IReadOnlyList<X509Certificate2> CertificateChain { get; }

    public byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm) => SignHash(hash, hashAlgorithm, RSASignaturePadding.Pkcs1);

    /// <summary>Signs with the given RSA padding (ignored for EC keys).</summary>
    public byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
    {
        using (var rsa = Certificate.GetRSAPrivateKey())
            if (rsa != null) return rsa.SignHash(hash, hashAlgorithm, padding);
        using (var ec = Certificate.GetECDsaPrivateKey())
            if (ec != null) return ec.SignHash(hash);
        throw new PdfException("Unsupported key type (supported: RSA, ECDSA).");
    }
}

/// <summary>RSA facade that forwards signing to an <see cref="IExternalSigner"/> (used by SignedCms).</summary>
internal sealed class ExternalRsa : RSA
{
    private readonly IExternalSigner _signer;
    private readonly RSA _publicKey;

    public ExternalRsa(IExternalSigner signer, RSA publicKey)
    {
        _signer = signer;
        _publicKey = publicKey;
        KeySizeValue = publicKey.KeySize;
        LegalKeySizesValue = new[] { new KeySizes(publicKey.KeySize, publicKey.KeySize, 0) };
    }

    public override RSAParameters ExportParameters(bool includePrivateParameters)
    {
        if (includePrivateParameters) throw new CryptographicException("The private key is held by an external signer.");
        return _publicKey.ExportParameters(false);
    }

    public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();

    public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) =>
        _signer.SignHash(hash, hashAlgorithm, padding);

    public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) =>
        _publicKey.VerifyHash(hash, signature, hashAlgorithm, padding);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _publicKey.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>ECDSA facade that forwards signing to an <see cref="IExternalSigner"/>.</summary>
internal sealed class ExternalECDsa : ECDsa
{
    private readonly IExternalSigner _signer;
    private readonly ECDsa _publicKey;
    private readonly HashAlgorithmName _hashAlgorithm;

    public ExternalECDsa(IExternalSigner signer, ECDsa publicKey, HashAlgorithmName hashAlgorithm)
    {
        _signer = signer;
        _publicKey = publicKey;
        _hashAlgorithm = hashAlgorithm;
        KeySizeValue = publicKey.KeySize;
        LegalKeySizesValue = new[] { new KeySizes(publicKey.KeySize, publicKey.KeySize, 0) };
    }

    public override byte[] SignHash(byte[] hash) => _signer.SignHash(hash, _hashAlgorithm);

    public override bool VerifyHash(byte[] hash, byte[] signature) => _publicKey.VerifyHash(hash, signature);

    public override ECParameters ExportParameters(bool includePrivateParameters)
    {
        if (includePrivateParameters) throw new CryptographicException("The private key is held by an external signer.");
        return _publicKey.ExportParameters(false);
    }

    public override ECParameters ExportExplicitParameters(bool includePrivateParameters) => _publicKey.ExportExplicitParameters(false);

    public override void ImportParameters(ECParameters parameters) => throw new NotSupportedException();

    public override void GenerateKey(ECCurve curve) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _publicKey.Dispose();
        base.Dispose(disposing);
    }
}
