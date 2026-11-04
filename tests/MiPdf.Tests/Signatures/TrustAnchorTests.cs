using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MiPdf.Signatures;

namespace MiPdf.Tests.Signatures;

public class TrustAnchorTests
{
    private static X509Certificate2 CreateCa(string name, RSA key, X509Certificate2? issuer, RSA? issuerKey)
    {
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var from = DateTimeOffset.Now.AddDays(-2);
        return issuer == null
            ? request.CreateSelfSigned(from, from.AddYears(5))
            : request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForRSA(issuerKey!, RSASignaturePadding.Pkcs1), from, from.AddYears(4), new byte[] { 2 });
    }

    private static byte[] SignWithIssuingCa(out X509Certificate2 issuingCa)
    {
        using var rootKey = RSA.Create(2048);
        using var caKey = RSA.Create(2048);
        using var signerKey = RSA.Create(2048);
        using var root = CreateCa("Test Root", rootKey, null, null);
        issuingCa = CreateCa("Test Issuing CA", caKey, root, rootKey);
        var request = new CertificateRequest("CN=Test Signer", signerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        var from = DateTimeOffset.Now.AddDays(-1);
        using var signer = request.Create(issuingCa.SubjectName, X509SignatureGenerator.CreateForRSA(caKey, RSASignaturePadding.Pkcs1), from, from.AddYears(1), new byte[] { 3 })
            .CopyWithPrivateKey(signerKey);

        var doc = PdfDocument.Create();
        doc.AddPage();
        return PdfSigner.Sign(doc.ToArray(), signer, chain: new[] { issuingCa }); // the root is not in the signature
    }

    [Fact]
    public void IssuingCaFromATrustedListIsATrustAnchor()
    {
        var pdf = SignWithIssuingCa(out var issuingCa);
        var result = Assert.Single(PdfSignatureValidator.Validate(pdf, new SignatureValidationOptions { CheckCertificateChain = true, TrustedRoots = { issuingCa } }));
        Assert.True(result.IsCertificateTrusted, string.Join("; ", result.Errors));
        Assert.True(result.IsIntegrityValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void UntrustedChainDoesNotAffectIntegrity()
    {
        var pdf = SignWithIssuingCa(out _);
        using var otherKey = RSA.Create(2048);
        using var other = CreateCa("Other Root", otherKey, null, null);
        var result = Assert.Single(PdfSignatureValidator.Validate(pdf, new SignatureValidationOptions { CheckCertificateChain = true, TrustedRoots = { other } }));
        Assert.False(result.IsCertificateTrusted);
        Assert.True(result.IsIntegrityValid); // the signed bytes and the signature value are correct
        Assert.Contains(result.Errors, e => e.StartsWith("Certificate chain", StringComparison.Ordinal));
    }
}
