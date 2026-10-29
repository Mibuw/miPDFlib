using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using MiPdf.Fonts;
using MiPdf.Signatures;

namespace MiPdf.Tests.Signatures;

/// <summary>An "HSM" that supports both RSA paddings.</summary>
internal sealed class PssHsm(X509Certificate2 certificate, RSA key) : IExternalSigner
{
    public List<RSASignaturePadding> Paddings { get; } = new();
    public X509Certificate2 Certificate => certificate;
    public IReadOnlyList<X509Certificate2> CertificateChain => Array.Empty<X509Certificate2>();

    public byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm) => SignHash(hash, hashAlgorithm, RSASignaturePadding.Pkcs1);

    public byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
    {
        Paddings.Add(padding);
        return key.SignHash(hash, hashAlgorithm, padding);
    }
}

/// <summary>ECDSA curves and digest sizes, RSASSA-PSS.</summary>
public class AlgorithmTests : IClassFixture<TestPki>
{
    private const string OidRsaPss = "1.2.840.113549.1.1.10";
    private readonly TestPki _pki;

    public AlgorithmTests(TestPki pki) => _pki = pki;

    public static TheoryData<string, string, string> Curves => new()
    {
        { "nistP256", "SHA256", "1.2.840.10045.4.3.2" },
        { "nistP384", "SHA384", "1.2.840.10045.4.3.3" },
        { "nistP521", "SHA512", "1.2.840.10045.4.3.4" },
        { "brainpoolP256r1", "SHA256", "1.2.840.10045.4.3.2" },
        { "brainpoolP384r1", "SHA384", "1.2.840.10045.4.3.3" },
        { "brainpoolP512r1", "SHA512", "1.2.840.10045.4.3.4" },
    };

    private static ECCurve Curve(string name) => name switch
    {
        "nistP256" => ECCurve.NamedCurves.nistP256,
        "nistP384" => ECCurve.NamedCurves.nistP384,
        "nistP521" => ECCurve.NamedCurves.nistP521,
        "brainpoolP256r1" => ECCurve.NamedCurves.brainpoolP256r1,
        "brainpoolP384r1" => ECCurve.NamedCurves.brainpoolP384r1,
        "brainpoolP512r1" => ECCurve.NamedCurves.brainpoolP512r1,
        _ => throw new ArgumentException(name),
    };

    private static byte[] CreatePdf()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas())
            c.DrawText("Algorithm test", PdfStandardFont.Helvetica, 12, 72, 700);
        return doc.ToArray();
    }

    private X509Certificate2 Issue(string subject, ECDsa key, RSASignaturePadding? caPadding = null)
    {
        var now = DateTimeOffset.UtcNow;
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        return req.Create(_pki.Ca.SubjectName, X509SignatureGenerator.CreateForRSA(_pki.CaKey, caPadding ?? RSASignaturePadding.Pkcs1),
            now.AddDays(-1), now.AddMonths(6), RandomNumberGenerator.GetBytes(8));
    }

    private SignatureValidationResult ValidateTrusted(byte[] signed)
    {
        var options = new SignatureValidationOptions { CheckCertificateChain = true };
        options.TrustedRoots.Add(_pki.Ca);
        var result = Assert.Single(PdfSignatureValidator.Validate(signed, options));
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.Errors));
        Assert.True(result.IsCertificateTrusted, string.Join("; ", result.Errors));
        return result;
    }

    private static SignerInfo SignerInfo(SignatureValidationResult result)
    {
        var cms = new SignedCms();
        cms.Decode(result.Cms!);
        return cms.SignerInfos[0];
    }

    /// <summary>DER of the signatureAlgorithm AlgorithmIdentifier of the first SignerInfo.</summary>
    private static byte[] SignatureAlgorithmDer(byte[] cms)
    {
        var contentInfo = new AsnReader(cms, AsnEncodingRules.BER).ReadSequence();
        contentInfo.ReadObjectIdentifier();
        var signedData = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        ReadOnlyMemory<byte> last = default;
        while (signedData.HasData) last = signedData.ReadEncodedValue();
        var signerInfo = new AsnReader(last, AsnEncodingRules.BER).ReadSetOf(skipSortOrderValidation: true).ReadSequence();
        ReadOnlyMemory<byte> previous = default;
        while (signerInfo.HasData)
        {
            if (signerInfo.PeekTag().HasSameClassAndValue(Asn1Tag.PrimitiveOctetString)) return previous.ToArray();
            previous = signerInfo.ReadEncodedValue();
        }
        throw new InvalidOperationException("no signature value");
    }

    [Theory]
    [MemberData(nameof(Curves))]
    public void EcdsaCurvesWithLocalExternalAndDeferredSigning(string curve, string hashName, string signatureOid)
    {
        var hash = new HashAlgorithmName(hashName);
        using var key = ECDsa.Create(Curve(curve));
        var certificate = Issue($"CN=EC {curve}", key);

        // 1. certificate with private key
        using (var withKey = certificate.CopyWithPrivateKey(key))
        {
            var signed = PdfSigner.Sign(CreatePdf(), withKey, new PdfSignatureOptions { DigestAlgorithm = hash });
            var signerInfo = SignerInfo(ValidateTrusted(signed));
            Assert.Equal(signatureOid, signerInfo.SignatureAlgorithm.Value);
            Assert.Equal(CmsBuilder.DigestOid(hash), signerInfo.DigestAlgorithm.Value);
        }

        // 2. external signer (HSM, smart card) returning IEEE P1363
        var hsm = new FakeHsm(certificate, (h, _) => key.SignHash(h));
        var external = PdfSigner.Sign(CreatePdf(), hsm, new PdfSignatureOptions { DigestAlgorithm = hash });
        Assert.Equal(1, hsm.Calls);
        Assert.Equal(signatureOid, SignerInfo(ValidateTrusted(external)).SignatureAlgorithm.Value);

        // 3. deferred signing with a raw signature value
        var prepared = PdfSigner.PrepareExternalSignature(CreatePdf(), new PdfSignatureOptions { DigestAlgorithm = hash }, certificate);
        var attributes = CmsUtil.CreateSignedAttributes(prepared.GetDocumentDigest(), certificate);
        var raw = key.SignHash(CmsBuilder.Hash(hash, attributes));
        Assert.Equal(2 * ((key.KeySize + 7) / 8), raw.Length);
        var deferred = PdfSigner.CompleteExternalSignature(prepared, CmsUtil.AssembleSignedData(attributes, raw, certificate, digestAlgorithm: hash));
        Assert.Equal(signatureOid, SignerInfo(ValidateTrusted(deferred)).SignatureAlgorithm.Value);
    }

    [Fact]
    public void RawEcdsaValueStartingWith0x30IsNotMistakenForDer()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certificate = Issue("CN=EC 0x30", key);
        var prepared = PdfSigner.PrepareExternalSignature(CreatePdf(), new PdfSignatureOptions(), certificate);
        var attributes = CmsUtil.CreateSignedAttributes(prepared.GetDocumentDigest(), certificate);
        var digest = SHA256.HashData(attributes);
        byte[] raw;
        int attempts = 0;
        do raw = key.SignHash(digest); // r is random: about one in 256 values starts with 0x30
        while (raw[0] != 0x30 && ++attempts < 20000);
        Assert.Equal(0x30, raw[0]);
        var signed = PdfSigner.CompleteExternalSignature(prepared, CmsUtil.AssembleSignedData(attributes, raw, certificate));
        ValidateTrusted(signed);

        // a DER value is passed through unchanged
        var prepared2 = PdfSigner.PrepareExternalSignature(CreatePdf(), new PdfSignatureOptions(), certificate);
        var attributes2 = CmsUtil.CreateSignedAttributes(prepared2.GetDocumentDigest(), certificate);
        var der = key.SignHash(SHA256.HashData(attributes2), DSASignatureFormat.Rfc3279DerSequence);
        ValidateTrusted(PdfSigner.CompleteExternalSignature(prepared2, CmsUtil.AssembleSignedData(attributes2, der, certificate)));
    }

    [Theory]
    [InlineData("SHA256")]
    [InlineData("SHA384")]
    [InlineData("SHA512")]
    public void RsaPssWithLocalKey(string hashName)
    {
        var hash = new HashAlgorithmName(hashName);
        var signed = PdfSigner.Sign(CreatePdf(), _pki.Rsa, new PdfSignatureOptions { DigestAlgorithm = hash, RsaPadding = RSASignaturePadding.Pss });
        var result = ValidateTrusted(signed);
        Assert.Equal(OidRsaPss, SignerInfo(result).SignatureAlgorithm.Value);

        // RSASSA-PSS-params: hash, MGF1 with the same hash, salt length = hash length
        var parameters = new AsnReader(SignatureAlgorithmDer(result.Cms!), AsnEncodingRules.DER).ReadSequence();
        Assert.Equal(OidRsaPss, parameters.ReadObjectIdentifier());
        var pss = parameters.ReadSequence();
        Assert.Equal(CmsBuilder.DigestOid(hash), pss.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence().ReadObjectIdentifier());
        var mgf = pss.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1)).ReadSequence();
        Assert.Equal("1.2.840.113549.1.1.8", mgf.ReadObjectIdentifier());
        Assert.Equal(CmsBuilder.DigestOid(hash), mgf.ReadSequence().ReadObjectIdentifier());
        Assert.Equal(CmsBuilder.Hash(hash, Array.Empty<byte>()).Length,
            (int)pss.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 2)).ReadInteger());
    }

    [Fact]
    public void RsaPssWithTimestamp()
    {
        var tsa = new FakeTsa(_pki);
        var signed = PdfSigner.SignDetailed(CreatePdf(), new CertificateSigner(_pki.Rsa),
            new PdfSignatureOptions { RsaPadding = RSASignaturePadding.Pss, TimestampClient = tsa });
        Assert.Equal(PadesLevel.BT, signed.Level);
        var result = ValidateTrusted(signed.Pdf);
        Assert.NotNull(result.TimestampTime);
        Assert.Equal(OidRsaPss, SignerInfo(result).SignatureAlgorithm.Value);
    }

    [Fact]
    public void RsaPssWithExternalSigner()
    {
        var hsm = new PssHsm(CmsBuilder.LoadCertificate(_pki.Rsa.RawData), _pki.RsaKey);
        var signed = PdfSigner.Sign(CreatePdf(), hsm, new PdfSignatureOptions { RsaPadding = RSASignaturePadding.Pss });
        Assert.Equal(RSASignaturePadding.Pss, Assert.Single(hsm.Paddings));
        Assert.Equal(OidRsaPss, SignerInfo(ValidateTrusted(signed)).SignatureAlgorithm.Value);

        // the default PKCS#1 v1.5 still goes through the new overload
        PdfSigner.Sign(CreatePdf(), hsm);
        Assert.Equal(RSASignaturePadding.Pkcs1, hsm.Paddings[1]);
    }

    [Fact]
    public void ExternalSignerWithoutPssSupportIsRejected()
    {
        var hsm = new FakeHsm(CmsBuilder.LoadCertificate(_pki.Rsa.RawData), (h, a) => _pki.RsaKey.SignHash(h, a, RSASignaturePadding.Pkcs1));
        var ex = Assert.ThrowsAny<Exception>(() => PdfSigner.Sign(CreatePdf(), hsm, new PdfSignatureOptions { RsaPadding = RSASignaturePadding.Pss }));
        Assert.Contains("RSASSA-PSS", ex.ToString());
        Assert.Equal(0, hsm.Calls);
    }

    [Fact]
    public void RsaPssDeferredMatchesSignedCms()
    {
        var prepared = PdfSigner.PrepareExternalSignature(CreatePdf(), new PdfSignatureOptions(), _pki.Rsa);
        var attributes = CmsUtil.CreateSignedAttributes(prepared.GetDocumentDigest(), _pki.Rsa);
        var signature = _pki.RsaKey.SignHash(SHA256.HashData(attributes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var cms = CmsUtil.AssembleSignedData(attributes, signature, _pki.Rsa, new[] { _pki.Ca }, rsaPadding: RSASignaturePadding.Pss);
        var result = ValidateTrusted(PdfSigner.CompleteExternalSignature(prepared, cms));
        Assert.Equal(OidRsaPss, SignerInfo(result).SignatureAlgorithm.Value);

        // same AlgorithmIdentifier as written by SignedCms
        var reference = PdfSigner.Sign(CreatePdf(), _pki.Rsa, new PdfSignatureOptions { RsaPadding = RSASignaturePadding.Pss });
        var referenceCms = Assert.Single(PdfSignatureValidator.Validate(reference)).Cms!;
        Assert.Equal(SignatureAlgorithmDer(referenceCms), SignatureAlgorithmDer(cms));
    }

    [Fact]
    public void CertificatesSignedWithRsaPss()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certificate = Issue("CN=Issued with PSS", key, RSASignaturePadding.Pss);
        Assert.True(CertificateSignature.Verify(certificate, _pki.Ca));
        Assert.False(CertificateSignature.Verify(certificate, _pki.Rsa));

        using var withKey = certificate.CopyWithPrivateKey(key);
        ValidateTrusted(PdfSigner.Sign(CreatePdf(), withKey));
    }
}
