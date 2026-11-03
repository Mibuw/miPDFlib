using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using MiPdf.Fonts;
using MiPdf.Objects;
using MiPdf.Signatures;

namespace MiPdf.Tests.Signatures;

/// <summary>
/// Signatures are created and verified from the digest of the signed byte ranges. Cross-checked with SignedCms in both
/// directions, and tampering must be detected.
/// </summary>
public class DigestVerificationTests : IClassFixture<TestPki>
{
    private readonly TestPki _pki;

    public DigestVerificationTests(TestPki pki) => _pki = pki;

    private static byte[] CreatePdf()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas())
            c.DrawText("Digest verification", PdfStandardFont.Helvetica, 12, 72, 700);
        return doc.ToArray();
    }

    private X509Certificate2 CreateEcCertificate(out ECDsa key)
    {
        key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new CertificateRequest("CN=EC signer", key, HashAlgorithmName.SHA384);
        var now = DateTimeOffset.UtcNow;
        return request.Create(_pki.Ca.SubjectName, X509SignatureGenerator.CreateForRSA(_pki.CaKey, RSASignaturePadding.Pkcs1),
            now.AddDays(-1), now.AddYears(1), new byte[] { 9, 9 }).CopyWithPrivateKey(key);
    }

    /// <summary>Verifies the signature of a signed PDF with SignedCms over the copied byte ranges.</summary>
    private static void CheckWithSignedCms(byte[] signed)
    {
        var result = Assert.Single(PdfSignatureValidator.Validate(signed));
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.Errors));
        var range = result.SignatureDictionary!.GetArray(PdfName.ByteRange)!.Select(o => (int)((PdfNumber)o).LongValue).ToArray();
        var data = signed.AsSpan(0, range[1]).ToArray().Concat(signed.AsSpan(range[2], range[3]).ToArray()).ToArray();
        var cms = new SignedCms(new ContentInfo(data), detached: true);
        cms.Decode(result.Cms!);
        cms.CheckSignature(verifySignatureOnly: true);
    }

    public static TheoryData<string> Variants => new() { "rsa", "rsa-pss", "rsa-sha512", "pkcs7", "ec" };

    [Theory]
    [MemberData(nameof(Variants))]
    public void CreatedSignaturesVerifyWithSignedCms(string variant)
    {
        var options = new PdfSignatureOptions();
        X509Certificate2 certificate = _pki.Rsa;
        ECDsa? key = null;
        switch (variant)
        {
            case "rsa-pss": options.RsaPadding = RSASignaturePadding.Pss; break;
            case "rsa-sha512": options.DigestAlgorithm = HashAlgorithmName.SHA512; break;
            case "pkcs7": options.Format = PdfSignatureFormat.Pkcs7Detached; break;
            case "ec":
                certificate = CreateEcCertificate(out key);
                options.DigestAlgorithm = HashAlgorithmName.SHA384;
                break;
        }
        using (key) CheckWithSignedCms(PdfSigner.Sign(CreatePdf(), certificate, options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignaturesCreatedWithSignedCmsAreAccepted(bool ec)
    {
        ECDsa? key = null;
        var certificate = ec ? CreateEcCertificate(out key) : _pki.Rsa;
        using (key)
        {
            var prepared = PdfSigner.PrepareExternalSignature(CreatePdf(), new PdfSignatureOptions(), certificate);
            var cms = new SignedCms(new ContentInfo(prepared.GetSignedData()), detached: true);
            var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") };
            if (!ec) signer.SignaturePadding = RSASignaturePadding.Pss;
            cms.ComputeSignature(signer, silent: true);
            var signed = PdfSigner.CompleteExternalSignature(prepared, cms.Encode());

            var result = Assert.Single(PdfSignatureValidator.Validate(signed));
            Assert.True(result.IsIntegrityValid, string.Join("; ", result.Errors));
        }
    }

    [Fact]
    public void ChangedSignatureValueIsDetected()
    {
        var signed = PdfSigner.Sign(CreatePdf(), _pki.Rsa);
        var result = Assert.Single(PdfSignatureValidator.Validate(signed));
        // without unsigned attributes the signature value is the end of the CMS; change its last byte in the hex string
        int end = (int)result.SignatureDictionary!.GetArray(PdfName.ByteRange)!.Select(o => ((PdfNumber)o).LongValue).ElementAt(1) + 1 + result.Cms!.Length * 2;
        signed[end - 1] = signed[end - 1] == (byte)'0' ? (byte)'1' : (byte)'0';

        var tampered = Assert.Single(PdfSignatureValidator.Validate(signed));
        Assert.False(tampered.IsIntegrityValid);
        Assert.Contains(tampered.Errors, e => e.Contains("signature value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SignatureOfAnotherDocumentIsDetected()
    {
        // a valid CMS, but over the digest of a different document
        var other = PdfSigner.PrepareExternalSignature(CreatePdf(), new PdfSignatureOptions { SigningTime = DateTimeOffset.UtcNow.AddMinutes(-5) });
        var attributes = CmsUtil.CreateSignedAttributes(other.GetDocumentDigest(), _pki.Rsa);
        var signature = _pki.RsaKey.SignHash(SHA256.HashData(attributes), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cms = CmsUtil.AssembleSignedData(attributes, signature, _pki.Rsa);

        var prepared = PdfSigner.PrepareExternalSignature(CreatePdf(), new PdfSignatureOptions());
        Assert.NotEqual(other.GetDocumentDigest(), prepared.GetDocumentDigest());
        var result = Assert.Single(PdfSignatureValidator.Validate(PdfSigner.CompleteExternalSignature(prepared, cms)));
        Assert.False(result.IsIntegrityValid);
        Assert.Contains(result.Errors, e => e.Contains("message digest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SignatureByAnotherKeyIsDetected()
    {
        var prepared = PdfSigner.PrepareExternalSignature(CreatePdf(), new PdfSignatureOptions());
        var attributes = CmsUtil.CreateSignedAttributes(prepared.GetDocumentDigest(), _pki.Rsa);
        using var otherKey = RSA.Create(2048);
        var signature = otherKey.SignHash(SHA256.HashData(attributes), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signed = PdfSigner.CompleteExternalSignature(prepared, CmsUtil.AssembleSignedData(attributes, signature, _pki.Rsa));

        var result = Assert.Single(PdfSignatureValidator.Validate(signed));
        Assert.False(result.IsIntegrityValid);
    }

    [Fact]
    public void DocumentDigestMatchesTheSignedData()
    {
        var prepared = PdfSigner.PrepareExternalSignature(CreatePdf(), new PdfSignatureOptions { DigestAlgorithm = HashAlgorithmName.SHA384 });
        Assert.Equal(SHA384.HashData(prepared.GetSignedData()), prepared.GetDocumentDigest());
    }
}
