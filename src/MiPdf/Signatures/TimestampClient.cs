using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;

namespace MiPdf.Signatures;

/// <summary>Obtains RFC 3161 time-stamp tokens.</summary>
public interface ITimestampClient
{
    /// <summary>Returns the DER-encoded time-stamp token (a CMS SignedData) for the given hash.</summary>
    byte[] GetTimestampToken(byte[] hash, HashAlgorithmName hashAlgorithm);
}

/// <summary>RFC 3161 time-stamp client over HTTP(S).</summary>
public sealed class HttpTimestampClient : ITimestampClient
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _http;

    /// <param name="url">URL of the time-stamp authority, e.g. "http://timestamp.digicert.com".</param>
    /// <param name="userName">Optional user for HTTP basic authentication.</param>
    /// <param name="password">Optional password for HTTP basic authentication.</param>
    /// <param name="httpClient">Optional HttpClient (proxies, client certificates, ...).</param>
    public HttpTimestampClient(string url, string? userName = null, string? password = null, HttpClient? httpClient = null)
    {
        Url = url;
        UserName = userName;
        Password = password;
        _http = httpClient ?? SharedClient;
    }

    public string Url { get; }

    public string? UserName { get; }

    public string? Password { get; }

    /// <summary>Maximum accepted response size in bytes (default 1 MB).</summary>
    public int MaxResponseSize { get; set; } = 1024 * 1024;

    /// <summary>Optional TSA policy OID.</summary>
    public string? PolicyOid { get; set; }

    public byte[] GetTimestampToken(byte[] hash, HashAlgorithmName hashAlgorithm)
    {
        var nonce = RandomNumberGenerator.GetBytes(8);
        nonce[0] &= 0x7F; // positive integer
        var request = Rfc3161TimestampRequest.CreateFromHash(hash, hashAlgorithm,
            PolicyOid != null ? new System.Security.Cryptography.Oid(PolicyOid) : null, nonce, requestSignerCertificates: true);
        using var message = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new ByteArrayContent(request.Encode()),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
        if (UserName != null)
        {
            var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{UserName}:{Password}"));
            message.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
        using var response = _http.Send(message);
        if (!response.IsSuccessStatusCode)
            throw new PdfException($"Time-stamp authority returned HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > MaxResponseSize) throw new PdfException("Time-stamp response too large.");
        using var stream = response.Content.ReadAsStream();
        using var ms = new MemoryStream();
        var buffer = new byte[16384];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > MaxResponseSize) throw new PdfException("Time-stamp response too large.");
        }
        var token = request.ProcessResponse(ms.ToArray(), out _);
        return token.AsSignedCms().Encode();
    }
}

/// <summary>Tries several time-stamp authorities in order and returns the first token obtained.</summary>
public sealed class FailoverTimestampClient : ITimestampClient
{
    private readonly IReadOnlyList<ITimestampClient> _clients;

    public FailoverTimestampClient(params ITimestampClient[] clients) : this((IEnumerable<ITimestampClient>)clients) { }

    public FailoverTimestampClient(IEnumerable<ITimestampClient> clients)
    {
        _clients = clients.ToList();
        if (_clients.Count == 0) throw new ArgumentException("At least one time-stamp client is required.", nameof(clients));
    }

    /// <summary>Creates a failover client for a list of TSA URLs (optionally with the same basic-auth credentials).</summary>
    public static FailoverTimestampClient FromUrls(IEnumerable<string> urls, string? userName = null, string? password = null) =>
        new(urls.Select(u => (ITimestampClient)new HttpTimestampClient(u, userName, password)));

    /// <summary>Errors of the failed attempts of the last call (empty if the first TSA succeeded).</summary>
    public IReadOnlyList<string> LastErrors { get; private set; } = Array.Empty<string>();

    public byte[] GetTimestampToken(byte[] hash, HashAlgorithmName hashAlgorithm)
    {
        var errors = new List<string>();
        foreach (var client in _clients)
        {
            try
            {
                var token = client.GetTimestampToken(hash, hashAlgorithm);
                LastErrors = errors;
                return token;
            }
            catch (Exception ex) when (ex is PdfException or HttpRequestException or CryptographicException or TaskCanceledException or IOException)
            {
                errors.Add($"{(client as HttpTimestampClient)?.Url ?? client.GetType().Name}: {ex.Message}");
            }
        }
        LastErrors = errors;
        throw new PdfException("No time-stamp authority answered: " + string.Join("; ", errors));
    }
}
