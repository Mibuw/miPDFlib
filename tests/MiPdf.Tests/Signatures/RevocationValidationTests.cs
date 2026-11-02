using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MiPdf.Fonts;
using MiPdf.Signatures;

namespace MiPdf.Tests.Signatures;

/// <summary>Revocation data signed by the test CA (or a delegated responder): OCSP and CRLs with chosen content.</summary>
internal sealed class SignedRevocationSource(TestPki pki) : IRevocationSource
{
    /// <summary>OCSP: null = no OCSP, otherwise the revocation time (DateTimeOffset.MaxValue = good).</summary>
    public DateTimeOffset? OcspRevokedAt { get; init; }

    /// <summary>Signs OCSP responses with this certificate and key instead of the CA.</summary>
    public (X509Certificate2 Certificate, RSA Key)? Responder { get; init; }

    /// <summary>CRL entries for the signer certificate (null = no CRL).</summary>
    public DateTimeOffset? CrlRevokedAt { get; init; }

    public bool Crl { get; init; }

    /// <summary>Signs the CRL with this CA instead of the test CA.</summary>
    public X509Certificate2? CrlIssuer { get; init; }

    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow.AddSeconds(2);

    public IReadOnlyList<X509Certificate2> GetIssuers(X509Certificate2 certificate) =>
        certificate.IssuerName.Name == pki.Ca.SubjectName.Name ? new[] { pki.Ca } : Array.Empty<X509Certificate2>();

    public byte[]? GetOcspResponse(X509Certificate2 certificate, X509Certificate2 issuer) =>
        OcspRevokedAt is { } revoked ? CreateOcsp(certificate, issuer, revoked == DateTimeOffset.MaxValue ? null : revoked) : null;

    public IReadOnlyList<byte[]> GetCrls(X509Certificate2 certificate)
    {
        if (!Crl) return Array.Empty<byte[]>();
        var builder = new CertificateRevocationListBuilder();
        if (CrlRevokedAt is { } at && certificate.Thumbprint == pki.Rsa.Thumbprint) builder.AddEntry(certificate, at);
        return new[] { builder.Build(CrlIssuer ?? pki.Ca, 1, IssuedAt.AddDays(7), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, IssuedAt) };
    }

    public byte[] CreateOcsp(X509Certificate2 certificate, X509Certificate2 issuer, DateTimeOffset? revokedAt)
    {
        var signer = Responder?.Certificate ?? pki.Ca;
        var tbs = new AsnWriter(AsnEncodingRules.DER);
        using (tbs.PushSequence())
        {
            using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true))) tbs.WriteEncodedValue(signer.SubjectName.RawData);
            tbs.WriteGeneralizedTime(IssuedAt, omitFractionalSeconds: true);
            using (tbs.PushSequence())
            using (tbs.PushSequence())
            {
                tbs.WriteEncodedValue(new AsnReader(RevocationInfo.CreateOcspRequest(certificate, issuer), AsnEncodingRules.DER)
                    .ReadSequence().ReadSequence().ReadSequence().ReadSequence().ReadEncodedValue().Span); // CertID
                if (revokedAt is { } at)
                    using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true))) tbs.WriteGeneralizedTime(at, omitFractionalSeconds: true);
                else tbs.WriteNull(new Asn1Tag(TagClass.ContextSpecific, 0));
                tbs.WriteGeneralizedTime(IssuedAt, omitFractionalSeconds: true);
            }
        }
        byte[] tbsDer = tbs.Encode();
        byte[] signature = (Responder?.Key ?? pki.CaKey).SignData(tbsDer, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var basic = new AsnWriter(AsnEncodingRules.DER);
        using (basic.PushSequence())
        {
            basic.WriteEncodedValue(tbsDer);
            using (basic.PushSequence())
            {
                basic.WriteObjectIdentifier("1.2.840.113549.1.1.11");
                basic.WriteNull();
            }
            basic.WriteBitString(signature);
            if (Responder is { } responder)
                using (basic.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                using (basic.PushSequence())
                    basic.WriteEncodedValue(responder.Certificate.RawData);
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
}

public class RevocationValidationTests : IClassFixture<TestPki>
{
    private readonly TestPki _pki;

    public RevocationValidationTests(TestPki pki) => _pki = pki;

    private static byte[] CreatePdf()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas()) c.DrawText("Contract", PdfStandardFont.Helvetica, 12, 72, 700);
        return doc.ToArray();
    }

    /// <summary>Signs with a time-stamp, adds the validation data of <paramref name="source"/> and validates.</summary>
    private SignatureValidationResult SignAndValidate(IRevocationSource source)
    {
        var signed = PdfSigner.Sign(CreatePdf(), _pki.Rsa, new PdfSignatureOptions { TimestampClient = new FakeTsa(_pki) });
        var lt = PdfLtv.AddValidationData(signed, new LtvOptions { RevocationSource = source });
        return PdfSignatureValidator.Validate(lt.Pdf).Single(r => !r.IsDocumentTimestamp);
    }

    [Fact]
    public void GoodOcspResponseAfterSigning()
    {
        var result = SignAndValidate(new SignedRevocationSource(_pki) { OcspRevokedAt = DateTimeOffset.MaxValue });
        Assert.Equal(RevocationStatus.Good, result.Revocation);
        var signer = Assert.Single(result.RevocationDetails);   // the self-signed root is not checked
        Assert.Equal(_pki.Rsa.Thumbprint, signer.Certificate.Thumbprint);
        Assert.Equal("OCSP", signer.Source);
        Assert.True(signer.IsFresh);
        Assert.Null(signer.RevocationTime);
    }

    [Fact]
    public void RevokedBeforeSigning()
    {
        var crl = SignAndValidate(new SignedRevocationSource(_pki) { Crl = true, CrlRevokedAt = DateTimeOffset.UtcNow.AddHours(-1) });
        Assert.Equal(RevocationStatus.Revoked, crl.Revocation);
        Assert.Equal("CRL", crl.RevocationDetails[0].Source);

        var ocsp = SignAndValidate(new SignedRevocationSource(_pki) { OcspRevokedAt = DateTimeOffset.UtcNow.AddHours(-1) });
        Assert.Equal(RevocationStatus.Revoked, ocsp.Revocation);
        Assert.Equal("OCSP", ocsp.RevocationDetails[0].Source);
        Assert.True(ocsp.IsIntegrityValid);   // integrity and revocation are reported separately
    }

    [Fact]
    public void RevokedAfterSigningKeepsTheSignatureGood()
    {
        var later = DateTimeOffset.UtcNow.AddHours(1);
        var result = SignAndValidate(new SignedRevocationSource(_pki) { Crl = true, CrlRevokedAt = later, IssuedAt = later.AddHours(1) });
        Assert.Equal(RevocationStatus.Good, result.Revocation);
        Assert.Equal(later.ToUnixTimeSeconds(), result.RevocationDetails[0].RevocationTime!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void UnsignedOrForeignDataIsNotTrusted()
    {
        // FakeRevocationSource produces OCSP responses with an invalid (all zero) signature
        var forged = SignAndValidate(new FakeRevocationSource(_pki, ocsp: true));
        Assert.Equal(RevocationStatus.Unknown, forged.Revocation);
        Assert.Null(forged.RevocationDetails[0].Source);

        // a CRL with the right issuer name, signed by another key
        using var otherKey = RSA.Create(2048);
        var otherReq = new CertificateRequest("CN=Test Root CA", otherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        otherReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        otherReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var otherCa = otherReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));
        var foreign = SignAndValidate(new SignedRevocationSource(_pki) { Crl = true, CrlIssuer = otherCa });
        Assert.Equal(RevocationStatus.Unknown, foreign.Revocation);
    }

    [Fact]
    public void DelegatedResponderNeedsTheOcspSigningPurpose()
    {
        RevocationStatus? With(bool ocspSigning)
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=Test OCSP Responder", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (ocspSigning) req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.9") }, true));
            req.CertificateExtensions.Add(new X509Extension("1.3.6.1.5.5.7.48.1.5", new byte[] { 0x05, 0x00 }, false)); // ocsp-nocheck
            var responder = req.Create(_pki.Ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), new byte[] { 7, 7, ocspSigning ? (byte)1 : (byte)0 });
            return SignAndValidate(new SignedRevocationSource(_pki) { OcspRevokedAt = DateTimeOffset.MaxValue, Responder = (responder, key) }).Revocation;
        }

        Assert.Equal(RevocationStatus.Good, With(ocspSigning: true));
        Assert.Equal(RevocationStatus.Unknown, With(ocspSigning: false));
    }

    [Fact]
    public void RevocationInfoArchivalInTheSignatureIsUsed()
    {
        var source = new SignedRevocationSource(_pki) { IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var archival = new AsnWriter(AsnEncodingRules.DER);
        using (archival.PushSequence())
        using (archival.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true)))
        using (archival.PushSequence())
            archival.WriteEncodedValue(source.CreateOcsp(_pki.Rsa, _pki.Ca, null));
        var options = new PdfSignatureOptions();
        options.AdditionalSignedAttributes.Add(new AsnEncodedData("1.2.840.113583.1.1.8", archival.Encode()));
        var signed = PdfSigner.Sign(CreatePdf(), _pki.Rsa, options);

        // without the issuer the OCSP signature cannot be checked
        Assert.Equal(RevocationStatus.Unknown, Assert.Single(PdfSignatureValidator.Validate(signed)).Revocation);

        var validation = new SignatureValidationOptions();
        validation.TrustedRoots.Add(_pki.Ca);
        var result = Assert.Single(PdfSignatureValidator.Validate(signed, validation));
        Assert.Equal(RevocationStatus.Good, result.Revocation);
        // fetched before signing, and without a time-stamp the time of validation counts
        Assert.False(result.RevocationDetails[0].IsFresh);
    }

    [Fact]
    public void WithoutRevocationDataNothingIsReported()
    {
        var signed = PdfSigner.Sign(CreatePdf(), _pki.Rsa);
        var result = Assert.Single(PdfSignatureValidator.Validate(signed));
        Assert.Null(result.Revocation);
        Assert.Empty(result.RevocationDetails);
    }

    [Fact]
    public void MalformedRevocationDataIsIgnored()
    {
        Assert.Null(EmbeddedRevocation.ParseOcsp(new byte[] { 0x30, 0x03, 0x0A, 0x01, 0x00 }));
        Assert.Null(EmbeddedRevocation.ParseOcsp(new byte[] { 1, 2, 3 }));
        Assert.Null(EmbeddedRevocation.ParseCrl(new byte[] { 0x30, 0x80, 0xFF }));
        var crls = new List<byte[]>();
        var ocsps = new List<byte[]>();
        EmbeddedRevocation.ParseRevocationInfoArchival(new byte[] { 0x30, 0x05, 0xA1 }, crls, ocsps);
        Assert.Empty(crls);
        Assert.Empty(ocsps);
    }
}
