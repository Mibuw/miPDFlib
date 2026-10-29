using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace MiPdf.Signatures;

/// <summary>
/// Provides validation data for long-term signatures (PAdES B-LT): issuer certificates, OCSP responses and CRLs.
/// Implementations may throw on network errors; <see cref="PdfLtv"/> records them as warnings.
/// </summary>
public interface IRevocationSource
{
    /// <summary>Returns the issuer certificate(s) of <paramref name="certificate"/> (e.g. via AIA caIssuers), or an empty list.</summary>
    IReadOnlyList<X509Certificate2> GetIssuers(X509Certificate2 certificate);

    /// <summary>Returns a DER-encoded OCSPResponse for <paramref name="certificate"/>, or null if no OCSP responder is available.</summary>
    byte[]? GetOcspResponse(X509Certificate2 certificate, X509Certificate2 issuer);

    /// <summary>Returns DER-encoded CRLs covering <paramref name="certificate"/>, or an empty list.</summary>
    IReadOnlyList<byte[]> GetCrls(X509Certificate2 certificate);
}

/// <summary>
/// Fetches issuer certificates (AIA caIssuers), OCSP responses (RFC 6960, HTTP POST) and CRLs (CRL distribution
/// points) over HTTP. Responses are cached per instance; reuse the instance to benefit from the cache.
/// </summary>
/// <remarks>
/// The URLs come from certificates inside the document and are therefore untrusted. By default only public
/// internet addresses can be reached (checked for every connection, including redirects), so that uploaded
/// documents cannot be used to probe internal services (SSRF). Set <see cref="AllowPrivateNetworks"/> for an
/// internal CA, or restrict the hosts with <see cref="AllowedHosts"/>. A caller-supplied <see cref="HttpClient"/>
/// is used as is, without these checks.
/// </remarks>
public sealed class HttpRevocationSource : IRevocationSource
{
    private static readonly Lazy<HttpClient> PublicOnlyClient = new(() => SafeHttp.CreateClient(false, null, TimeSpan.FromSeconds(30)));
    private readonly HttpClient? _custom;
    private HttpClient? _configured;
    private readonly ConcurrentDictionary<string, (DateTime Expires, byte[] Data)> _cache = new();

    public HttpRevocationSource(HttpClient? httpClient = null) => _custom = httpClient;

    /// <summary>Allow connections to private, loopback and link-local addresses (default false).</summary>
    public bool AllowPrivateNetworks { get; init; }

    /// <summary>If set, only these host names may be contacted.</summary>
    public IReadOnlyCollection<string>? AllowedHosts { get; init; }

    /// <summary>Maximum number of cached responses (default 500).</summary>
    public int MaxCacheEntries { get; set; } = 500;

    private HttpClient Http => _custom ?? (AllowPrivateNetworks || AllowedHosts is { Count: > 0 }
        ? _configured ??= SafeHttp.CreateClient(AllowPrivateNetworks, AllowedHosts, TimeSpan.FromSeconds(30))
        : PublicOnlyClient.Value);

    /// <summary>CRLs larger than this are skipped (some CAs publish CRLs of more than 100 MB). Default 5 MB.</summary>
    public long MaxCrlSize { get; set; } = 5 * 1024 * 1024;

    /// <summary>How long downloaded CRLs and issuer certificates are cached (OCSP responses are not cached). Default 15 minutes.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(15);

    public IReadOnlyList<X509Certificate2> GetIssuers(X509Certificate2 certificate)
    {
        var result = new List<X509Certificate2>();
        foreach (var url in RevocationInfo.GetCaIssuerUrls(certificate).Take(3))
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            var data = Get(url, 1024 * 1024, cache: true);
            result.AddRange(RevocationInfo.ParseCertificates(data));
            if (result.Count > 0) break;
        }
        return result;
    }

    public byte[]? GetOcspResponse(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        foreach (var url in RevocationInfo.GetOcspUrls(certificate).Take(3))
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(RevocationInfo.CreateOcspRequest(certificate, issuer)) };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ocsp-request");
            using var response = Http.Send(message);
            if (!response.IsSuccessStatusCode) throw new PdfException($"OCSP responder {url} returned HTTP {(int)response.StatusCode}.");
            var data = ReadLimited(response, 1024 * 1024, url);
            var parsed = OcspResponse.Parse(data);
            if (parsed.Status != 0) throw new PdfException($"OCSP responder {url} answered with status {parsed.Status}.");
            return data;
        }
        return null;
    }

    public IReadOnlyList<byte[]> GetCrls(X509Certificate2 certificate)
    {
        foreach (var url in RevocationInfo.GetCrlUrls(certificate).Take(3))
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            var data = Get(url, MaxCrlSize, cache: true);
            if (data.Length > 0 && data[0] != 0x30) data = PemToDer(data);
            return new[] { data };
        }
        return Array.Empty<byte[]>();
    }

    private byte[] Get(string url, long limit, bool cache)
    {
        if (cache && _cache.TryGetValue(url, out var entry) && entry.Expires > DateTime.UtcNow) return entry.Data;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) throw new PdfException($"{url} returned HTTP {(int)response.StatusCode}.");
        var data = ReadLimited(response, limit, url);
        if (cache)
        {
            if (_cache.Count >= MaxCacheEntries)
            {
                foreach (var (key, value) in _cache)
                    if (value.Expires <= DateTime.UtcNow) _cache.TryRemove(key, out _);
                if (_cache.Count >= MaxCacheEntries) _cache.Clear();
            }
            _cache[url] = (DateTime.UtcNow + CacheDuration, data);
        }
        return data;
    }

    private static byte[] ReadLimited(HttpResponseMessage response, long limit, string url)
    {
        if (response.Content.Headers.ContentLength > limit) throw new PdfException($"{url}: response too large ({response.Content.Headers.ContentLength} bytes).");
        using var stream = response.Content.ReadAsStream();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > limit) throw new PdfException($"{url}: response too large (more than {limit} bytes).");
        }
        return ms.ToArray();
    }

    private static byte[] PemToDer(byte[] data)
    {
        var text = System.Text.Encoding.ASCII.GetString(data);
        int start = text.IndexOf("-----BEGIN", StringComparison.Ordinal);
        if (start < 0) return data;
        int body = text.IndexOf('\n', start) + 1;
        int end = text.IndexOf("-----END", body, StringComparison.Ordinal);
        return Convert.FromBase64String(text[body..end]);
    }
}

/// <summary>Parsed essentials of an OCSP response.</summary>
internal sealed class OcspResponse
{
    /// <summary>OCSPResponseStatus (0 = successful).</summary>
    public int Status { get; private init; }

    /// <summary>Status of the first SingleResponse: 0 good, 1 revoked, 2 unknown, -1 not available.</summary>
    public int CertStatus { get; private init; } = -1;

    public DateTimeOffset? ProducedAt { get; private init; }

    /// <summary>Certificates embedded in the response (e.g. a delegated OCSP responder certificate).</summary>
    public List<X509Certificate2> Certificates { get; } = new();

    public static OcspResponse Parse(byte[] data)
    {
        try
        {
            var outer = new AsnReader(data, AsnEncodingRules.BER).ReadSequence();
            int status = outer.ReadEnumeratedBytes().Span[^1];
            if (status != 0 || !outer.HasData) return new OcspResponse { Status = status };
            var responseBytes = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadSequence();
            responseBytes.ReadObjectIdentifier();
            var basic = new AsnReader(responseBytes.ReadOctetString(), AsnEncodingRules.BER).ReadSequence();
            var tbs = basic.ReadSequence();
            if (tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0, true))) tbs.ReadEncodedValue(); // version
            tbs.ReadEncodedValue(); // responderID
            var producedAt = tbs.ReadGeneralizedTime();
            var responses = tbs.ReadSequence();
            int certStatus = -1;
            if (responses.HasData)
            {
                var single = responses.ReadSequence();
                single.ReadEncodedValue(); // certID
                certStatus = single.PeekTag().TagValue;
            }
            var result = new OcspResponse { Status = 0, CertStatus = certStatus, ProducedAt = producedAt };
            basic.ReadEncodedValue(); // signatureAlgorithm
            basic.ReadEncodedValue(); // signature
            if (basic.HasData && basic.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            {
                var certs = basic.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadSequence();
                while (certs.HasData) result.Certificates.Add(CmsBuilder.LoadCertificate(certs.ReadEncodedValue().ToArray()));
            }
            return result;
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException)
        {
            throw new PdfException("Invalid OCSP response: " + ex.Message, ex);
        }
    }
}

/// <summary>Certificate extensions and request encoding for revocation checking.</summary>
public static class RevocationInfo
{
    private const string OidAia = "1.3.6.1.5.5.7.1.1";
    private const string OidCrlDistributionPoints = "2.5.29.31";
    private const string OidOcspNoCheck = "1.3.6.1.5.5.7.48.1.5";

    /// <summary>OCSP responder URLs from the Authority Information Access extension.</summary>
    public static IReadOnlyList<string> GetOcspUrls(X509Certificate2 certificate) => Aia(certificate, ocsp: true);

    /// <summary>CA issuer certificate URLs from the Authority Information Access extension.</summary>
    public static IReadOnlyList<string> GetCaIssuerUrls(X509Certificate2 certificate) => Aia(certificate, ocsp: false);

    private static IReadOnlyList<string> Aia(X509Certificate2 certificate, bool ocsp)
    {
        if (certificate.Extensions[OidAia] is not { } ext) return Array.Empty<string>();
        var aia = new X509AuthorityInformationAccessExtension(ext.RawData, ext.Critical);
        return (ocsp ? aia.EnumerateOcspUris() : aia.EnumerateCAIssuersUris()).ToList();
    }

    /// <summary>CRL distribution point URLs.</summary>
    public static IReadOnlyList<string> GetCrlUrls(X509Certificate2 certificate)
    {
        var urls = new List<string>();
        if (certificate.Extensions[OidCrlDistributionPoints] is not { } ext) return urls;
        try
        {
            var points = new AsnReader(ext.RawData, AsnEncodingRules.DER).ReadSequence();
            while (points.HasData)
            {
                var point = points.ReadSequence();
                if (!point.HasData || !point.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0, true))) continue;
                var name = point.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
                if (!name.HasData || !name.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0, true))) continue;
                var fullName = name.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
                while (fullName.HasData)
                {
                    var tag = fullName.PeekTag();
                    if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 6)
                        urls.Add(fullName.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6)));
                    else fullName.ReadEncodedValue();
                }
            }
        }
        catch (AsnContentException)
        {
            // ignore malformed extension
        }
        return urls;
    }

    /// <summary>True for certificates that must not be checked for revocation (OCSP responders with id-pkix-ocsp-nocheck).</summary>
    public static bool HasOcspNoCheck(X509Certificate2 certificate) => certificate.Extensions[OidOcspNoCheck] != null;

    /// <summary>True if the certificate is self-signed (a root): subject equals issuer.</summary>
    public static bool IsSelfSigned(X509Certificate2 certificate) => certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);

    /// <summary>Encodes an OCSP request (RFC 6960) with a SHA-1 CertID and without nonce (cacheable).</summary>
    public static byte[] CreateOcspRequest(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())            // OCSPRequest
        using (writer.PushSequence())            // TBSRequest
        using (writer.PushSequence())            // requestList
        using (writer.PushSequence())            // Request
        using (writer.PushSequence())            // CertID
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("1.3.14.3.2.26");
                writer.WriteNull();
            }
            writer.WriteOctetString(SHA1.HashData(certificate.IssuerName.RawData));
            writer.WriteOctetString(SHA1.HashData(issuer.PublicKey.EncodedKeyValue.RawData));
            writer.WriteInteger(certificate.SerialNumberBytes.Span);
        }
        return writer.Encode();
    }

    /// <summary>Parses a DER certificate or a PKCS#7 certificate bundle (.p7c).</summary>
    internal static IEnumerable<X509Certificate2> ParseCertificates(byte[] data)
    {
        try
        {
            return new[] { CmsBuilder.LoadCertificate(data) };
        }
        catch (CryptographicException)
        {
            var cms = new SignedCms();
            cms.Decode(data);
            return cms.Certificates.Cast<X509Certificate2>().ToList();
        }
    }
}

/// <summary>Verifies the signature of a certificate with the public key of a candidate issuer.</summary>
internal static class CertificateSignature
{
    /// <summary>True/false if the signature could be checked, null for unsupported algorithms.</summary>
    public static bool? Verify(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        try
        {
            var cert = new AsnReader(certificate.RawData, AsnEncodingRules.DER).ReadSequence();
            var tbs = cert.ReadEncodedValue();
            var algorithm = cert.ReadSequence();
            string oid = algorithm.ReadObjectIdentifier();
            var signature = cert.ReadBitString(out _);
            if (oid == CmsUtil.OidRsaPss)
            {
                if (!algorithm.HasData || PssHash(algorithm.ReadSequence()) is not { } pssHash) return null;
                using var pssKey = issuer.GetRSAPublicKey();
                return pssKey?.VerifyData(tbs.Span, signature, pssHash, RSASignaturePadding.Pss);
            }
            HashAlgorithmName? hash = oid switch
            {
                "1.2.840.113549.1.1.5" or "1.2.840.10045.4.1" => HashAlgorithmName.SHA1,
                "1.2.840.113549.1.1.11" or "1.2.840.10045.4.3.2" => HashAlgorithmName.SHA256,
                "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
                "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
                _ => null,
            };
            if (hash is not { } h) return null;
            if (oid.StartsWith("1.2.840.113549", StringComparison.Ordinal))
            {
                using var rsa = issuer.GetRSAPublicKey();
                return rsa?.VerifyData(tbs.Span, signature, h, RSASignaturePadding.Pkcs1);
            }
            using var ec = issuer.GetECDsaPublicKey();
            return ec?.VerifyData(tbs.Span, signature, h, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Hash of RSASSA-PSS-params if MGF1 uses the same hash and the salt is as long as the hash (else null).</summary>
    private static HashAlgorithmName? PssHash(AsnReader parameters)
    {
        string hashOid = "1.3.14.3.2.26", mgfHashOid = "1.3.14.3.2.26";
        int saltLength = 20;
        var context0 = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
        var context1 = new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true);
        var context2 = new Asn1Tag(TagClass.ContextSpecific, 2, isConstructed: true);
        if (parameters.HasData && parameters.PeekTag().HasSameClassAndValue(context0))
            hashOid = parameters.ReadSequence(context0).ReadSequence().ReadObjectIdentifier();
        if (parameters.HasData && parameters.PeekTag().HasSameClassAndValue(context1))
        {
            var mgf = parameters.ReadSequence(context1).ReadSequence();
            if (mgf.ReadObjectIdentifier() != "1.2.840.113549.1.1.8") return null;
            mgfHashOid = mgf.ReadSequence().ReadObjectIdentifier();
        }
        if (parameters.HasData && parameters.PeekTag().HasSameClassAndValue(context2))
            saltLength = (int)parameters.ReadSequence(context2).ReadInteger();
        HashAlgorithmName? hash = hashOid switch
        {
            "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
            "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
            "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
            "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
            _ => null,
        };
        int expectedSalt = hashOid switch { "1.3.14.3.2.26" => 20, "2.16.840.1.101.3.4.2.2" => 48, "2.16.840.1.101.3.4.2.3" => 64, _ => 32 };
        return hash != null && mgfHashOid == hashOid && saltLength == expectedSalt ? hash : null;
    }
}
