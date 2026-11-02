using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MiPdf.Signatures;

/// <summary>Revocation status of a certificate at the time of signing.</summary>
public enum RevocationStatus
{
    /// <summary>Valid revocation data shows that the certificate was not revoked at the time of signing.</summary>
    Good,

    /// <summary>The certificate was revoked at or before the time of signing.</summary>
    Revoked,

    /// <summary>No usable revocation data (missing, not signed by an authorised party, or the issuer is unknown).</summary>
    Unknown,
}

/// <summary>Revocation status of one certificate of the signer's chain, from the revocation data embedded in the document.</summary>
/// <param name="Certificate">The checked certificate.</param>
/// <param name="Status">Status at the time of signing.</param>
/// <param name="Source">"OCSP" or "CRL" for the evidence used, null if there is none.</param>
/// <param name="IssuedAt">Time of the evidence (OCSP thisUpdate, CRL thisUpdate).</param>
/// <param name="RevocationTime">Revocation time if the evidence lists the certificate as revoked – also when the
/// revocation happened after signing and the status is therefore <see cref="RevocationStatus.Good"/>.</param>
/// <param name="IsFresh">The evidence was issued at or after the time of signing, so it covers that time.</param>
public sealed record CertificateRevocation(X509Certificate2 Certificate, RevocationStatus Status, string? Source,
    DateTimeOffset? IssuedAt, DateTimeOffset? RevocationTime, bool IsFresh);

/// <summary>Evaluates OCSP responses and CRLs stored in a document (DSS, adbe-revocationInfoArchival).</summary>
internal static class EmbeddedRevocation
{
    internal const string OidRevocationInfoArchival = "1.2.840.113583.1.1.8";
    private const string OidOcspSigning = "1.3.6.1.5.5.7.3.9";

    /// <summary>Upper bound for parsed items per kind, against documents stuffed with validation data.</summary>
    internal const int MaxItems = 1000;

    internal sealed record SingleResponse(string HashOid, byte[] IssuerNameHash, byte[] IssuerKeyHash, BigInteger Serial,
        int Status, DateTimeOffset? RevocationTime, DateTimeOffset ThisUpdate);

    internal sealed class Ocsp
    {
        public required byte[] Basic { get; init; }
        public byte[]? ResponderName { get; init; }
        public byte[]? ResponderKeyHash { get; init; }
        public List<SingleResponse> Responses { get; } = new();
        public List<X509Certificate2> Certificates { get; } = new();
    }

    internal sealed class Crl
    {
        public required byte[] Der { get; init; }
        public required byte[] Issuer { get; init; }
        public DateTimeOffset ThisUpdate { get; init; }
        public Dictionary<BigInteger, DateTimeOffset> Revoked { get; } = new();
    }

    /// <summary>Checks the chain of <paramref name="signer"/> up to a self-signed or trusted root at <paramref name="time"/>.</summary>
    public static (RevocationStatus Status, List<CertificateRevocation> Details) Evaluate(X509Certificate2 signer,
        IReadOnlyList<X509Certificate2> pool, IReadOnlyCollection<X509Certificate2> trustedRoots,
        IReadOnlyList<Ocsp> ocsps, IReadOnlyList<Crl> crls, DateTimeOffset time)
    {
        var details = new List<CertificateRevocation>();
        var current = signer;
        var seen = new HashSet<string>();
        for (int depth = 0; depth < 10 && seen.Add(current.Thumbprint); depth++)
        {
            if (RevocationInfo.IsSelfSigned(current) || trustedRoots.Any(r => r.Thumbprint == current.Thumbprint)) break;
            var issuer = FindIssuer(current, pool);
            if (issuer == null)
            {
                details.Add(new CertificateRevocation(current, RevocationStatus.Unknown, null, null, null, false));
                break;
            }
            details.Add(Check(current, issuer, pool, ocsps, crls, time));
            current = issuer;
        }
        var status = details.Any(d => d.Status == RevocationStatus.Revoked) ? RevocationStatus.Revoked
            : details.Any(d => d.Status == RevocationStatus.Unknown) ? RevocationStatus.Unknown
            : RevocationStatus.Good;
        return (status, details);
    }

    private static CertificateRevocation Check(X509Certificate2 certificate, X509Certificate2 issuer, IReadOnlyList<X509Certificate2> pool,
        IReadOnlyList<Ocsp> ocsps, IReadOnlyList<Crl> crls, DateTimeOffset time)
    {
        // all evidence that is validly signed and concerns this certificate: (source, issued at, revocation time)
        var evidence = new List<(string Source, DateTimeOffset IssuedAt, DateTimeOffset? Revoked)>();
        var serial = new BigInteger(certificate.SerialNumberBytes.Span, isUnsigned: false, isBigEndian: true);
        foreach (var ocsp in ocsps)
        {
            var matching = ocsp.Responses.Where(r => r.Serial == serial && r.Status is 0 or 1 && MatchesIssuer(r, certificate, issuer)).ToList();
            if (matching.Count == 0 || !IsAuthorisedResponse(ocsp, issuer, pool)) continue;
            foreach (var r in matching) evidence.Add(("OCSP", r.ThisUpdate, r.Status == 1 ? r.RevocationTime ?? DateTimeOffset.MinValue : null));
        }
        foreach (var crl in crls)
        {
            if (!crl.Issuer.AsSpan().SequenceEqual(certificate.IssuerName.RawData) || CertificateSignature.VerifySigned(crl.Der, issuer) != true) continue;
            evidence.Add(("CRL", crl.ThisUpdate, crl.Revoked.TryGetValue(serial, out var revoked) ? revoked : null));
        }

        // a revocation is final: any evidence of a revocation before the time of signing wins
        var revokedBefore = evidence.Where(e => e.Revoked <= time).OrderBy(e => e.Revoked).FirstOrDefault();
        if (revokedBefore.Source != null)
            return new CertificateRevocation(certificate, RevocationStatus.Revoked, revokedBefore.Source, revokedBefore.IssuedAt, revokedBefore.Revoked, revokedBefore.IssuedAt >= time);
        if (evidence.Count == 0) return new CertificateRevocation(certificate, RevocationStatus.Unknown, null, null, null, false);
        var best = evidence.OrderByDescending(e => e.IssuedAt).First();
        var later = evidence.Where(e => e.Revoked.HasValue).Select(e => e.Revoked).Min();
        return new CertificateRevocation(certificate, RevocationStatus.Good, best.Source, best.IssuedAt, later, best.IssuedAt >= time);
    }

    private static bool MatchesIssuer(SingleResponse response, X509Certificate2 certificate, X509Certificate2 issuer)
    {
        HashAlgorithmName? hash = response.HashOid switch
        {
            "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
            "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
            "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
            "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
            _ => null,
        };
        if (hash is not { } h) return false;
        return Hash(h, certificate.IssuerName.RawData).AsSpan().SequenceEqual(response.IssuerNameHash)
            && Hash(h, issuer.PublicKey.EncodedKeyValue.RawData).AsSpan().SequenceEqual(response.IssuerKeyHash);
    }

    private static byte[] Hash(HashAlgorithmName algorithm, byte[] data) => algorithm.Name switch
    {
        "SHA1" => SHA1.HashData(data),
        "SHA384" => SHA384.HashData(data),
        "SHA512" => SHA512.HashData(data),
        _ => SHA256.HashData(data),
    };

    /// <summary>The response is signed by the issuer itself or by a responder certificate the issuer authorised (RFC 6960, 4.2.2.2).</summary>
    private static bool IsAuthorisedResponse(Ocsp ocsp, X509Certificate2 issuer, IReadOnlyList<X509Certificate2> pool)
    {
        foreach (var candidate in ocsp.Certificates.Concat(pool).Append(issuer))
        {
            bool identified = ocsp.ResponderName != null ? candidate.SubjectName.RawData.AsSpan().SequenceEqual(ocsp.ResponderName)
                : ocsp.ResponderKeyHash != null && SHA1.HashData(candidate.PublicKey.EncodedKeyValue.RawData).AsSpan().SequenceEqual(ocsp.ResponderKeyHash);
            if (!identified) continue;
            bool isIssuer = candidate.RawData.AsSpan().SequenceEqual(issuer.RawData);
            bool delegated = !isIssuer && candidate.IssuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData)
                && CertificateSignature.VerifySigned(candidate.RawData, issuer) == true && HasOcspSigning(candidate);
            if ((isIssuer || delegated) && CertificateSignature.VerifySigned(ocsp.Basic, candidate) == true) return true;
        }
        return false;
    }

    private static bool HasOcspSigning(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == OidOcspSigning));

    private static X509Certificate2? FindIssuer(X509Certificate2 certificate, IReadOnlyList<X509Certificate2> pool) =>
        pool.FirstOrDefault(c => c.Thumbprint != certificate.Thumbprint
            && c.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData)
            && CertificateSignature.Verify(certificate, c) == true);

    /// <summary>Parses an OCSPResponse (RFC 6960); null if it is malformed or not a successful basic response.</summary>
    public static Ocsp? ParseOcsp(byte[] data)
    {
        try
        {
            var outer = new AsnReader(data, AsnEncodingRules.BER).ReadSequence();
            if (outer.ReadEnumeratedBytes().Span[^1] != 0 || !outer.HasData) return null;
            var responseBytes = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadSequence();
            if (responseBytes.ReadObjectIdentifier() != "1.3.6.1.5.5.7.48.1.1") return null;
            byte[] basicDer = responseBytes.ReadOctetString();
            var basic = new AsnReader(basicDer, AsnEncodingRules.BER).ReadSequence();
            var tbs = basic.ReadSequence();
            if (tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0, true))) tbs.ReadEncodedValue(); // version
            byte[]? responderName = null, responderKeyHash = null;
            var responderTag = tbs.PeekTag();
            var responder = tbs.ReadSequence(responderTag);
            if (responderTag.TagValue == 1) responderName = responder.ReadEncodedValue().ToArray();
            else responderKeyHash = responder.ReadOctetString();
            tbs.ReadGeneralizedTime(); // producedAt
            var result = new Ocsp { Basic = basicDer, ResponderName = responderName, ResponderKeyHash = responderKeyHash };
            var responses = tbs.ReadSequence();
            while (responses.HasData && result.Responses.Count < MaxItems)
            {
                var single = responses.ReadSequence();
                var certId = single.ReadSequence();
                string hashOid = certId.ReadSequence().ReadObjectIdentifier();
                byte[] nameHash = certId.ReadOctetString(), keyHash = certId.ReadOctetString();
                var serial = certId.ReadInteger();
                var statusTag = single.PeekTag();
                DateTimeOffset? revocationTime = null;
                if (statusTag.TagValue == 1) revocationTime = single.ReadSequence(statusTag).ReadGeneralizedTime();
                else single.ReadEncodedValue();
                var thisUpdate = single.ReadGeneralizedTime();
                result.Responses.Add(new SingleResponse(hashOid, nameHash, keyHash, serial, statusTag.TagValue, revocationTime, thisUpdate));
            }
            basic.ReadEncodedValue(); // signatureAlgorithm
            basic.ReadEncodedValue(); // signature
            if (basic.HasData && basic.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            {
                var certs = basic.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadSequence();
                while (certs.HasData && result.Certificates.Count < 20) result.Certificates.Add(CmsBuilder.LoadCertificate(certs.ReadEncodedValue().ToArray()));
            }
            return result;
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Parses a CRL (RFC 5280, 5.1); null if it is malformed.</summary>
    public static Crl? ParseCrl(byte[] data)
    {
        try
        {
            var list = new AsnReader(data, AsnEncodingRules.DER).ReadSequence();
            var tbs = list.ReadSequence();
            if (tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Integer)) tbs.ReadInteger(); // version
            tbs.ReadSequence(); // signature algorithm
            byte[] issuer = tbs.ReadEncodedValue().ToArray();
            var crl = new Crl { Der = data, Issuer = issuer, ThisUpdate = ReadTime(tbs) };
            if (tbs.HasData && IsTime(tbs.PeekTag())) ReadTime(tbs); // nextUpdate
            if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
            {
                var revoked = tbs.ReadSequence();
                while (revoked.HasData)
                {
                    var entry = revoked.ReadSequence();
                    var serial = entry.ReadInteger();
                    crl.Revoked.TryAdd(serial, ReadTime(entry));
                }
            }
            return crl;
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Reads the CRLs and OCSP responses of an adbe-revocationInfoArchival attribute value.</summary>
    public static void ParseRevocationInfoArchival(byte[] value, List<byte[]> crls, List<byte[]> ocsps)
    {
        try
        {
            var archival = new AsnReader(value, AsnEncodingRules.BER).ReadSequence();
            while (archival.HasData)
            {
                var tag = archival.PeekTag();
                if (tag.TagClass != TagClass.ContextSpecific || tag.TagValue > 1)
                {
                    archival.ReadEncodedValue();
                    continue;
                }
                var items = archival.ReadSequence(tag).ReadSequence();
                var target = tag.TagValue == 0 ? crls : ocsps;
                while (items.HasData && target.Count < MaxItems) target.Add(items.ReadEncodedValue().ToArray());
            }
        }
        catch (AsnContentException)
        {
            // a malformed attribute provides no revocation data
        }
    }

    private static bool IsTime(Asn1Tag tag) => tag.HasSameClassAndValue(new Asn1Tag(UniversalTagNumber.UtcTime))
        || tag.HasSameClassAndValue(new Asn1Tag(UniversalTagNumber.GeneralizedTime));

    private static DateTimeOffset ReadTime(AsnReader reader) =>
        reader.PeekTag().HasSameClassAndValue(new Asn1Tag(UniversalTagNumber.UtcTime)) ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();
}
