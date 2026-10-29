using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MiPdf.Fonts;
using MiPdf.Forms;
using MiPdf.IO;
using MiPdf.Objects;
using MiPdf.Security;
using MiPdf.Signatures;

namespace MiPdf.Tests.Signatures;

/// <summary>Test PKI shared by the signature tests (root CA, RSA signer, ECDSA signer).</summary>
public sealed class TestPki : IDisposable
{
    public TestPki()
    {
        // one fixed point in time: leaf certificates must not outlive the CA (even by a second)
        var now = DateTimeOffset.UtcNow;
        CaKey = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=Test Root CA", CaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        Ca = caReq.CreateSelfSigned(now.AddDays(-2), now.AddYears(2));

        RsaKey = RSA.Create(2048);
        var req = new CertificateRequest("CN=Max Mustermann, O=Test", RsaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Rsa = req.Create(Ca, now.AddDays(-1), now.AddYears(1), new byte[] { 1, 2, 3, 4 }).CopyWithPrivateKey(RsaKey);

        EcKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ecReq = new CertificateRequest("CN=Erika EC", EcKey, HashAlgorithmName.SHA256);
        EcPublic = ecReq.Create(Ca.SubjectName, X509SignatureGenerator.CreateForRSA(CaKey, RSASignaturePadding.Pkcs1),
            now.AddDays(-1), now.AddYears(1), new byte[] { 5, 6, 7, 8 });
    }

    public RSA CaKey { get; }
    public X509Certificate2 Ca { get; }
    public RSA RsaKey { get; }
    public X509Certificate2 Rsa { get; }
    public ECDsa EcKey { get; }
    public X509Certificate2 EcPublic { get; }

    public void Dispose()
    {
        CaKey.Dispose();
        RsaKey.Dispose();
        EcKey.Dispose();
    }
}

/// <summary>An "HSM" that only exposes hash signing.</summary>
internal sealed class FakeHsm(X509Certificate2 certificate, Func<byte[], HashAlgorithmName, byte[]> sign) : IExternalSigner
{
    public int Calls { get; private set; }
    public X509Certificate2 Certificate => certificate;
    public IReadOnlyList<X509Certificate2> CertificateChain => Array.Empty<X509Certificate2>();

    public byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm)
    {
        Calls++;
        return sign(hash, hashAlgorithm);
    }
}

public class SignatureTests : IClassFixture<TestPki>
{
    private readonly TestPki _pki;

    public SignatureTests(TestPki pki) => _pki = pki;

    private static byte[] CreatePdf(int pages = 1)
    {
        var doc = PdfDocument.Create();
        for (int i = 0; i < pages; i++)
            using (var c = doc.AddPage().GetCanvas())
                c.DrawText($"Contract page {i + 1}", PdfStandardFont.Helvetica, 12, 72, 700);
        return doc.ToArray();
    }

    private SignatureValidationOptions TrustTestCa()
    {
        var o = new SignatureValidationOptions { CheckCertificateChain = true };
        o.TrustedRoots.Add(_pki.Ca);
        return o;
    }

    [Theory]
    [InlineData(PdfSignatureFormat.PAdES)]
    [InlineData(PdfSignatureFormat.Pkcs7Detached)]
    public void SignAndValidate(PdfSignatureFormat format)
    {
        var original = CreatePdf();
        var signed = PdfSigner.Sign(original, _pki.Rsa, new PdfSignatureOptions { Format = format, Reason = "Test", Location = "Wien" }, new[] { _pki.Ca });
        Assert.True(signed.AsSpan(0, original.Length).SequenceEqual(original), "signing must be an incremental update");

        var result = Assert.Single(PdfSignatureValidator.Validate(signed, TrustTestCa()));
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.Errors));
        Assert.True(result.CoversWholeDocument);
        Assert.True(result.IsCertificateTrusted);
        Assert.Equal("Max Mustermann", result.SignerName);
        Assert.Equal("Test", result.Reason);
        Assert.Equal(format == PdfSignatureFormat.PAdES ? "ETSI.CAdES.detached" : "adbe.pkcs7.detached", result.SubFilter);
        Assert.Equal(_pki.Rsa.Thumbprint, result.SignerCertificate!.Thumbprint);
    }

    [Fact]
    public void PadesContainsSigningCertificateV2AndNoSigningTime()
    {
        var signed = PdfSigner.Sign(CreatePdf(), _pki.Rsa);
        var sig = PdfDocument.Open(signed).GetForm()!.GetField<PdfSignatureField>("Signature1")!.SignatureDictionary!;
        var cms = new SignedCms();
        cms.Decode(CmsBuilder.TrimDer(sig.GetString(PdfName.Contents)!.Bytes));
        var oids = cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>().Select(a => a.Oid.Value).ToList();
        Assert.Contains("1.2.840.113549.1.9.16.2.47", oids); // signing-certificate-v2
        Assert.Contains("1.2.840.113549.1.9.4", oids);       // message digest
        Assert.DoesNotContain("1.2.840.113549.1.9.5", oids); // no signing-time in PAdES
        Assert.Equal(CmsBuilder.DigestOid(HashAlgorithmName.SHA256), cms.SignerInfos[0].DigestAlgorithm.Value);
    }

    [Fact]
    public void TamperingIsDetected()
    {
        var signed = PdfSigner.Sign(CreatePdf(), _pki.Rsa);
        var text = Encoding.Latin1.GetString(signed);
        int index = text.IndexOf("/Producer", StringComparison.Ordinal);
        signed[index + 2] = (byte)'X'; // change a byte inside the signed range
        var result = Assert.Single(PdfSignatureValidator.Validate(signed));
        Assert.False(result.IsIntegrityValid);
    }

    [Fact]
    public void MultipleSignaturesStayValid()
    {
        var first = PdfSigner.Sign(CreatePdf(), _pki.Rsa, new PdfSignatureOptions { Rectangle = new PdfRectangle(50, 50, 250, 100) });
        var hsm = new FakeHsm(_pki.EcPublic, (hash, _) => _pki.EcKey.SignHash(hash));
        var second = PdfSigner.Sign(first, hsm, new PdfSignatureOptions { Rectangle = new PdfRectangle(300, 50, 500, 100) });
        Assert.Equal(1, hsm.Calls);

        var results = PdfSignatureValidator.Validate(second, TrustTestCa());
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IsIntegrityValid, string.Join("; ", r.Errors)));
        Assert.False(results[0].CoversWholeDocument);
        Assert.True(results[1].CoversWholeDocument);
        Assert.Equal("Erika EC", results[1].SignerName);
        Assert.Equal(first.Length, PdfSignatureValidator.GetSignedRevision(second, results[0]).Length);
    }

    [Fact]
    public void ExternalRsaSigner()
    {
        var hsm = new FakeHsm(CmsBuilder.LoadCertificate(_pki.Rsa.RawData),
            (hash, alg) => _pki.RsaKey.SignHash(hash, alg, RSASignaturePadding.Pkcs1));
        var signed = PdfSigner.Sign(CreatePdf(), hsm, new PdfSignatureOptions { DigestAlgorithm = HashAlgorithmName.SHA512 });
        Assert.Equal(1, hsm.Calls);
        Assert.True(Assert.Single(PdfSignatureValidator.Validate(signed)).IsIntegrityValid);
    }

    [Fact]
    public void DeferredExternalSignature()
    {
        // step 1: prepare and send the data to a "signing service"
        var prepared = PdfSigner.PrepareExternalSignature(CreatePdf(), new PdfSignatureOptions { FieldName = "remote", Reason = "Remote" }, _pki.Rsa);
        var dataToSign = prepared.GetSignedData();
        Assert.Equal(prepared.GetDocumentDigest(), SHA256.HashData(dataToSign));

        // the service creates a detached CMS container
        var cms = new SignedCms(new ContentInfo(dataToSign), detached: true);
        cms.ComputeSignature(new CmsSigner(_pki.Rsa), silent: true);
        var container = cms.Encode();

        // step 2: embed (here via the stored bytes and the field name)
        var signed = PdfSigner.CompleteExternalSignature(prepared.PreparedPdf, "remote", container);
        var result = Assert.Single(PdfSignatureValidator.Validate(signed));
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.Errors));
        Assert.Equal("remote", result.FieldName);
    }

    [Fact]
    public void ChangesAfterSigningAreReported()
    {
        var signed = PdfSigner.Sign(CreatePdf(), _pki.Rsa);
        var doc = PdfDocument.Open(signed);
        doc.Info.Title = "changed later";
        var updated = doc.ToArray(new PdfSaveOptions { Incremental = true });
        var result = Assert.Single(PdfSignatureValidator.Validate(updated));
        Assert.True(result.IsIntegrityValid);
        Assert.False(result.CoversWholeDocument);
    }

    [Fact]
    public void VisibleSignatureOnExistingField()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        doc.GetOrCreateForm().AddSignatureField(page, "Approval", new PdfRectangle(100, 100, 300, 160));
        var signed = PdfSigner.Sign(doc.ToArray(), _pki.Rsa, new PdfSignatureOptions { FieldName = "Approval", Reason = "OK" });
        var reopened = PdfDocument.Open(signed);
        var field = reopened.GetForm()!.GetField<PdfSignatureField>("Approval")!;
        Assert.True(field.IsSigned);
        var ap = field.Widgets[0].Dictionary.GetDictionary(PdfName.AP)!.GetStream(PdfName.N)!;
        Assert.Contains("Mustermann", reopened.GetPage(1).ExtractText(new MiPdf.Text.TextExtractionOptions { IncludeAnnotations = true }));
        Assert.True(ap.GetBytes().Length > 0);
        Assert.Equal(3, reopened.GetForm()!.Dictionary.GetInt(PdfName.SigFlags));
        Assert.Throws<PdfException>(() => PdfSigner.Sign(signed, _pki.Rsa, new PdfSignatureOptions { FieldName = "Approval" }));
    }

    [Fact]
    public void CertificationSignature()
    {
        var signed = PdfSigner.Sign(CreatePdf(), _pki.Rsa, new PdfSignatureOptions { Certification = CertificationLevel.FormFilling });
        var result = Assert.Single(PdfSignatureValidator.Validate(signed));
        Assert.Equal(CertificationLevel.FormFilling, result.CertificationLevel);
        var perms = PdfDocument.Open(signed).Catalog.GetDictionary(PdfName.Perms)!;
        Assert.NotNull(perms.GetDictionary("DocMDP"));
        Assert.Throws<PdfException>(() => PdfSigner.Sign(signed, _pki.Rsa, new PdfSignatureOptions { Certification = CertificationLevel.NoChanges }));
    }

    [Fact]
    public void SignEncryptedDocument()
    {
        var doc = PdfDocument.Open(CreatePdf());
        doc.Encrypt(new PdfEncryptionOptions { UserPassword = "user", OwnerPassword = "owner" });
        var encrypted = doc.ToArray();
        var signed = PdfSigner.Sign(encrypted, _pki.Rsa, new PdfSignatureOptions { Password = "owner", Reason = "Encrypted" });
        var result = Assert.Single(PdfSignatureValidator.Validate(signed, password: "user"));
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.Errors));
        Assert.Equal("Encrypted", result.Reason);
    }

    [Fact]
    public void SignatureOnDocumentWithXrefStreams()
    {
        var doc = PdfDocument.Open(CreatePdf(3));
        var compact = doc.ToArray(new PdfSaveOptions { UseObjectStreams = true });
        var signed = PdfSigner.Sign(compact, _pki.Rsa, new PdfSignatureOptions { PageNumber = 2, Rectangle = new PdfRectangle(10, 10, 200, 60) });
        var reopened = PdfDocument.Open(signed);
        Assert.False(reopened.WasRepaired);
        Assert.True(Assert.Single(PdfSignatureValidator.Validate(signed)).IsIntegrityValid);
        Assert.Single(reopened.GetPage(2).Annotations);
    }

    [Fact]
    public void TooSmallReservedSpaceThrows() =>
        Assert.Throws<PdfException>(() => PdfSigner.Sign(CreatePdf(), _pki.Rsa, new PdfSignatureOptions { SignatureSize = 100 }));
}
