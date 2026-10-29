using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.Signatures;

namespace MiPdf.Tests.Signatures;

/// <summary>A local RFC 3161 time-stamp authority signed by the test CA.</summary>
internal sealed class FakeTsa : ITimestampClient
{
    private readonly X509Certificate2 _certificate;

    public FakeTsa(TestPki pki)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test TSA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.8") }, true));
        _certificate = req.Create(pki.Ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), new byte[] { 9, 9 }).CopyWithPrivateKey(key);
    }

    public X509Certificate2 Certificate => _certificate;

    public int Calls { get; private set; }

    public byte[] GetTimestampToken(byte[] hash, HashAlgorithmName hashAlgorithm)
    {
        Calls++;
        var info = new Rfc3161TimestampTokenInfo(new Oid("1.2.3.4.5"), new Oid(CmsBuilder.DigestOid(hashAlgorithm)), hash,
            new byte[] { (byte)(Calls + 1) }, DateTimeOffset.UtcNow);
        var cms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()), detached: false);
        var signer = new CmsSigner(_certificate) { IncludeOption = X509IncludeOption.EndCertOnly, DigestAlgorithm = new Oid(CmsBuilder.DigestOid(HashAlgorithmName.SHA256)) };
        signer.SignedAttributes.Add(new AsnEncodedData(CmsBuilder.OidSigningCertificateV2, CmsBuilder.SigningCertificateV2(_certificate)));
        cms.ComputeSignature(signer, silent: true);
        return cms.Encode();
    }
}

internal sealed class FailingTsa : ITimestampClient
{
    public byte[] GetTimestampToken(byte[] hash, HashAlgorithmName hashAlgorithm) => throw new HttpRequestException("TSA down");
}

/// <summary>Offline revocation data: a CRL of the test CA and synthetic "good" OCSP responses.</summary>
internal sealed class FakeRevocationSource(TestPki pki, bool ocsp) : IRevocationSource
{
    public List<string> OcspRequests { get; } = new();

    public IReadOnlyList<X509Certificate2> GetIssuers(X509Certificate2 certificate) =>
        certificate.IssuerName.Name == pki.Ca.SubjectName.Name ? new[] { pki.Ca } : Array.Empty<X509Certificate2>();

    // responses are cached per certificate like a real responder does within their validity: a fresh response
    // (producedAt in a later second) would legitimately count as new validation data
    private readonly Dictionary<string, byte[]> _ocspResponses = new();

    public byte[]? GetOcspResponse(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        if (!ocsp) return null;
        OcspRequests.Add(certificate.Subject);
        if (!_ocspResponses.TryGetValue(certificate.Thumbprint, out var response))
            _ocspResponses[certificate.Thumbprint] = response = CreateOcspResponse(certificate, issuer);
        return response;
    }

    private static byte[] CreateOcspResponse(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        var basic = new AsnWriter(AsnEncodingRules.DER);
        using (basic.PushSequence())
        {
            using (basic.PushSequence())                                           // tbsResponseData
            {
                using (basic.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true))) basic.WriteEncodedValue(issuer.SubjectName.RawData);
                basic.WriteGeneralizedTime(DateTimeOffset.UtcNow, omitFractionalSeconds: true);
                using (basic.PushSequence())
                using (basic.PushSequence())                                       // SingleResponse
                {
                    basic.WriteEncodedValue(new AsnReader(RevocationInfo.CreateOcspRequest(certificate, issuer), AsnEncodingRules.DER)
                        .ReadSequence().ReadSequence().ReadSequence().ReadSequence().ReadEncodedValue().Span); // CertID
                    basic.WriteNull(new Asn1Tag(TagClass.ContextSpecific, 0));    // good
                    basic.WriteGeneralizedTime(DateTimeOffset.UtcNow, omitFractionalSeconds: true);
                }
            }
            using (basic.PushSequence()) basic.WriteObjectIdentifier("1.2.840.113549.1.1.11");
            basic.WriteBitString(new byte[256]);
        }
        var outer = new AsnWriter(AsnEncodingRules.DER);
        using (outer.PushSequence())
        {
            outer.WriteEncodedValue(new byte[] { 0x0A, 0x01, 0x00 }); // ENUMERATED successful(0)
            using (outer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            using (outer.PushSequence())
            {
                outer.WriteObjectIdentifier("1.3.6.1.5.5.7.48.1.1");
                outer.WriteOctetString(basic.Encode());
            }
        }
        return outer.Encode();
    }

    private byte[]? _crl;

    public IReadOnlyList<byte[]> GetCrls(X509Certificate2 certificate) =>
        new[] { _crl ??= new CertificateRevocationListBuilder().Build(pki.Ca, 1, DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1) };
}

public class AdvancedSignatureTests : IClassFixture<TestPki>
{
    private const string BiometricOid = "1.3.6.1.4.1.32473.1.1";
    private readonly TestPki _pki;

    public AdvancedSignatureTests(TestPki pki) => _pki = pki;

    private static byte[] CreatePdf()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas()) c.DrawText("Contract", PdfStandardFont.Helvetica, 12, 72, 700);
        return doc.ToArray();
    }

    private static byte[] OctetString(byte[] value)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        w.WriteOctetString(value);
        return w.Encode();
    }

    [Fact]
    public void AdditionalSignedAttributeIsSignedAndReadable()
    {
        var blob = RandomNumberGenerator.GetBytes(3000);
        var options = new PdfSignatureOptions();
        options.AdditionalSignedAttributes.Add(new AsnEncodedData(BiometricOid, OctetString(blob)));
        var signed = PdfSigner.Sign(CreatePdf(), _pki.Rsa, options);
        var result = Assert.Single(PdfSignatureValidator.Validate(signed));
        Assert.True(result.IsIntegrityValid);
        var value = result.GetSignedAttribute(BiometricOid)!;
        Assert.Equal(blob, new AsnReader(value, AsnEncodingRules.DER).ReadOctetString());
        Assert.Null(result.GetSignedAttribute("1.2.3"));
        Assert.Contains(result.Certificates, c => c.Thumbprint == _pki.Rsa.Thumbprint);
    }

    [Fact]
    public void TimestampFailoverAndOptionalTimestamp()
    {
        var tsa = new FakeTsa(_pki);
        var failover = new FailoverTimestampClient(new FailingTsa(), tsa);
        var result = PdfSigner.SignDetailed(CreatePdf(), new CertificateSigner(_pki.Rsa), new PdfSignatureOptions { TimestampClient = failover });
        Assert.Equal(PadesLevel.BT, result.Level);
        Assert.Single(failover.LastErrors);
        var validation = Assert.Single(PdfSignatureValidator.Validate(result.Pdf));
        Assert.True(validation.IsIntegrityValid);
        Assert.True(validation.HasTimestamp);
        Assert.Equal(PadesLevel.BT, validation.Level);

        var optional = PdfSigner.SignDetailed(CreatePdf(), new CertificateSigner(_pki.Rsa),
            new PdfSignatureOptions { TimestampClient = new FailingTsa(), TimestampOptional = true });
        Assert.Equal(PadesLevel.BB, optional.Level);
        Assert.Contains("TSA down", optional.Warnings[0]);
        Assert.True(PdfSignatureValidator.Validate(optional.Pdf)[0].IsIntegrityValid);

        Assert.ThrowsAny<Exception>(() => PdfSigner.Sign(CreatePdf(), _pki.Rsa, new PdfSignatureOptions { TimestampClient = new FailingTsa() }));
    }

    [Fact]
    public void ExternalCmsGetsTimestampWithoutReencoding()
    {
        var prepared = PdfSigner.PrepareExternalSignature(CreatePdf(), new PdfSignatureOptions(), _pki.Rsa, 32768);
        // a "foreign" CMS from a signing service
        var cms = new SignedCms(new ContentInfo(prepared.GetSignedData()), detached: true);
        var signer = new CmsSigner(_pki.Rsa);
        signer.SignedAttributes.Add(new AsnEncodedData(BiometricOid, OctetString(new byte[] { 1, 2, 3 })));
        cms.ComputeSignature(signer, silent: true);
        var original = cms.Encode();
        var stamped = CmsUtil.AddSignatureTimestamp(original, new FakeTsa(_pki));
        Assert.Equal(CmsUtil.GetSignatureValue(original), CmsUtil.GetSignatureValue(stamped));
        Assert.NotNull(CmsUtil.GetUnsignedAttribute(stamped, CmsBuilder.OidTimestampToken));

        var signed = PdfSigner.CompleteExternalSignature(prepared, stamped);
        var result = Assert.Single(PdfSignatureValidator.Validate(signed));
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.Errors));
        Assert.True(result.HasTimestamp);
        Assert.NotNull(result.GetSignedAttribute(BiometricOid));
    }

    [Fact]
    public void DeferredSigningWithRawRsaSignatureValue()
    {
        var prepared = PdfSigner.PrepareExternalSignature(CreatePdf(), new PdfSignatureOptions(), _pki.Rsa, 16384);
        var attributes = CmsUtil.CreateSignedAttributes(prepared.GetDocumentDigest(), _pki.Rsa,
            additionalAttributes: new[] { new AsnEncodedData(BiometricOid, OctetString(new byte[] { 42 })) });
        // ... the hash of the attributes travels to the signing service, the signature value comes back later
        var signature = _pki.RsaKey.SignHash(SHA256.HashData(attributes), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cms = CmsUtil.AssembleSignedData(attributes, signature, _pki.Rsa, new[] { _pki.Ca });
        var signed = PdfSigner.CompleteExternalSignature(prepared, cms);
        var result = Assert.Single(PdfSignatureValidator.Validate(signed, new SignatureValidationOptions { CheckCertificateChain = true, TrustedRoots = { _pki.Ca } }));
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.Errors));
        Assert.True(result.IsCertificateTrusted);
        Assert.NotNull(result.GetSignedAttribute(CmsBuilder.OidSigningCertificateV2));
    }

    [Fact]
    public void DeferredSigningWithRawEcdsaSignatureValue()
    {
        var prepared = PdfSigner.PrepareExternalSignature(CreatePdf(), new PdfSignatureOptions(), _pki.EcPublic, 16384);
        var attributes = CmsUtil.CreateSignedAttributes(prepared.GetDocumentDigest(), _pki.EcPublic);
        var raw = _pki.EcKey.SignHash(SHA256.HashData(attributes)); // IEEE P1363 r || s, as returned by many signing services
        Assert.Equal(64, raw.Length);
        var signed = PdfSigner.CompleteExternalSignature(prepared, CmsUtil.AssembleSignedData(attributes, raw, _pki.EcPublic));
        var result = Assert.Single(PdfSignatureValidator.Validate(signed));
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void CustomAppearanceWithInkAndTopImage()
    {
        var strokes = new List<IReadOnlyList<InkPoint>>
        {
            new[] { new InkPoint(10, 10, 0.2), new InkPoint(40, 30, 0.8), new InkPoint(80, 12, 1) },
            new[] { new InkPoint(100, 20, 0.5) },
        };
        var options = new PdfSignatureOptions
        {
            Rectangle = new PdfRectangle(300, 50, 500, 120),
            Appearance = new PdfSignatureAppearance
            {
                Text = "",
                Draw = (canvas, w, h) => InkRenderer.Draw(canvas, strokes, topLeftHeight: h),
            },
        };
        var signed = PdfSigner.Sign(CreatePdf(), _pki.Rsa, options);
        Assert.True(PdfSignatureValidator.Validate(signed)[0].IsIntegrityValid);
        var widget = PdfDocument.Open(signed).GetPage(1).Annotations.Single(a => a.Subtype == "Widget");
        var ap = (PdfStream)widget.Dictionary.GetDictionary(PdfName.AP)!.Get(PdfName.N)!;
        var content = Encoding.Latin1.GetString(ap.GetBytes());
        Assert.Contains("1 J", content);           // round caps
        Assert.Contains(" l\n", content);           // stroke segments
        Assert.Contains("1 0 0 -1 0 70 cm", content); // top-left origin flipped
        Assert.DoesNotContain("Tj", content);

        var png = TestHelpers.CreatePng(2, 2, new byte[16], alpha: true);
        var withImage = PdfSigner.Sign(CreatePdf(), _pki.Rsa, new PdfSignatureOptions
        {
            Rectangle = new PdfRectangle(300, 50, 500, 120),
            SignerName = "Max Mustermann",
            Appearance = new PdfSignatureAppearance { Image = MiPdf.Images.PdfImage.FromBytes(png), ImagePosition = SignatureImagePosition.Top, Text = "Signed by {name}" },
        });
        var imageWidget = PdfDocument.Open(withImage).GetPage(1).Annotations.Single(a => a.Subtype == "Widget");
        var imageAp = Encoding.Latin1.GetString(((PdfStream)imageWidget.Dictionary.GetDictionary(PdfName.AP)!.Get(PdfName.N)!).GetBytes());
        Assert.Contains("(Signed by Max Mustermann) Tj", imageAp);
        Assert.Contains(" Do", imageAp);
    }

    [Fact]
    public void DamagedDocumentsCanBeRepairedBeforeSigning()
    {
        var pdf = CreatePdf();
        var text = Encoding.Latin1.GetString(pdf);
        int startxref = text.LastIndexOf("startxref", StringComparison.Ordinal);
        var damaged = Encoding.Latin1.GetBytes(text[..startxref] + "startxref\n999999\n%%EOF\n");
        Assert.True(PdfDocument.Open(damaged).WasRepaired);
        Assert.Throws<PdfException>(() => PdfSigner.Sign(damaged, _pki.Rsa));
        var signed = PdfSigner.Sign(damaged, _pki.Rsa, new PdfSignatureOptions { RepairDamagedDocument = true });
        Assert.True(PdfSignatureValidator.Validate(signed)[0].IsIntegrityValid);
        Assert.False(PdfDocument.Open(signed).WasRepaired);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LongTermValidationAddsDss(bool ocsp)
    {
        var tsa = new FakeTsa(_pki);
        var signed = PdfSigner.Sign(CreatePdf(), _pki.Rsa, new PdfSignatureOptions { TimestampClient = tsa });
        var source = new FakeRevocationSource(_pki, ocsp);
        var options = new LtvOptions { RevocationSource = source, IncludeVri = true };
        var lt = PdfLtv.AddValidationData(signed, options);
        Assert.True(lt.IsComplete, string.Join("; ", lt.Warnings));
        Assert.Equal(3, lt.CertificatesAdded); // signer, TSA, root (fetched via the issuer source)
        Assert.Equal(ocsp ? 2 : 0, lt.OcspResponsesAdded);
        Assert.Equal(ocsp ? 0 : 1, lt.CrlsAdded);  // one CRL of the CA covers both certificates
        Assert.StartsWith(Encoding.Latin1.GetString(signed), Encoding.Latin1.GetString(lt.Pdf)); // incremental update

        var doc = PdfDocument.Open(lt.Pdf);
        var dss = doc.Catalog.GetDictionary(PdfName.Get("DSS"))!;
        Assert.Equal(3, dss.GetArray(PdfName.Get("Certs"))!.Count);
        var vri = dss.GetDictionary(PdfName.Get("VRI"))!;
        Assert.Single(vri.Keys);
        Assert.Equal(40, vri.Keys.First().Value.Length);
        Assert.NotNull(doc.Catalog.GetDictionary(PdfName.Get("Extensions"))!.GetDictionary(PdfName.Get("ESIC")));

        var result = Assert.Single(PdfSignatureValidator.Validate(lt.Pdf));
        Assert.True(result.IsIntegrityValid);
        Assert.Equal(PadesLevel.BLT, result.Level);

        // idempotent: nothing new on a second run
        var again = PdfLtv.AddValidationData(lt.Pdf, new LtvOptions { RevocationSource = source });
        Assert.Equal(0, again.CertificatesAdded + again.OcspResponsesAdded + again.CrlsAdded);
        Assert.Same(lt.Pdf, again.Pdf);

        var lta = PdfLtv.MakeLta(signed, tsa, new LtvOptions { RevocationSource = source });
        Assert.Equal(PadesLevel.BLTA, lta.Level);
        var results = PdfSignatureValidator.Validate(lta.Pdf);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IsIntegrityValid, string.Join("; ", r.Errors)));
        Assert.Equal(PadesLevel.BLTA, results.Single(r => !r.IsDocumentTimestamp).Level);
        Assert.Equal("DocTimeStamp", PdfDocument.Open(lta.Pdf).GetForm()!.Fields.Last().Dictionary.GetDictionary(PdfName.V)!.GetName(PdfName.Type)!.Value);
    }

    [Fact]
    public void LongTermValidationReportsMissingData()
    {
        var signed = PdfSigner.Sign(CreatePdf(), _pki.Rsa);
        var lt = PdfLtv.AddValidationData(signed, new LtvOptions { RevocationSource = new EmptySource() });
        Assert.False(lt.IsComplete);
        Assert.Contains(lt.Warnings, w => w.Contains("Issuer certificate"));
        Assert.Equal(1, lt.CertificatesAdded);
        var lta = PdfLtv.MakeLta(signed, new FailingTsa(), new LtvOptions { RevocationSource = new EmptySource() });
        Assert.Equal(PadesLevel.BLT, lta.Level);
        Assert.Contains(lta.Warnings, w => w.Contains("time-stamp failed"));
    }

    private sealed class EmptySource : IRevocationSource
    {
        public IReadOnlyList<X509Certificate2> GetIssuers(X509Certificate2 certificate) => Array.Empty<X509Certificate2>();
        public byte[]? GetOcspResponse(X509Certificate2 certificate, X509Certificate2 issuer) => null;
        public IReadOnlyList<byte[]> GetCrls(X509Certificate2 certificate) => Array.Empty<byte[]>();
    }

    [Fact]
    public void RevocationInfoParsesExtensions()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=With URLs", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(new[] { "http://ocsp.example.com" }, new[] { "http://ca.example.com/ca.cer" }));
        req.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(new[] { "http://crl.example.com/ca.crl" }));
        var cert = req.Create(_pki.Ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), new byte[] { 7 });
        Assert.Equal(new[] { "http://ocsp.example.com" }, RevocationInfo.GetOcspUrls(cert));
        Assert.Equal(new[] { "http://ca.example.com/ca.cer" }, RevocationInfo.GetCaIssuerUrls(cert));
        Assert.Equal(new[] { "http://crl.example.com/ca.crl" }, RevocationInfo.GetCrlUrls(cert));
        Assert.False(RevocationInfo.IsSelfSigned(cert));
        Assert.True(RevocationInfo.IsSelfSigned(_pki.Ca));
        Assert.True(CertificateSignature.Verify(cert, _pki.Ca));
        Assert.False(CertificateSignature.Verify(cert, _pki.Rsa));
    }

    [Fact]
    public void DrawTextBoxWrapsAndAligns()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        using (var c = page.GetCanvas())
            c.DrawTextBox("Erika Mustermann wohnt in der Musterstraße 1 in Wien", PdfStandardFont.Helvetica, 10,
                new PdfRectangle(100, 500, 200, 600), TextAlignment.Center, MiPdf.Layout.VerticalAlignment.Middle);
        var words = MiPdf.Text.PdfTextExtractor.ExtractWords(PdfDocument.Open(doc.ToArray()).GetPage(1));
        Assert.True(words.Count >= 8);
        Assert.All(words, w => Assert.InRange(w.Bounds.Left, 99.9, 200.1));
        Assert.All(words, w => Assert.InRange(w.Bounds.Bottom, 500, 600));
        Assert.True(words.Select(w => Math.Round(w.Bounds.Bottom)).Distinct().Count() >= 3); // wrapped into several lines
    }
}
