using System.Net;
using System.Net.Sockets;

namespace MiPdf.Signatures;

/// <summary>
/// HTTP clients for URLs taken from untrusted input (certificate extensions in uploaded documents). The connect
/// callback checks every resolved address, so redirects and DNS rebinding cannot reach internal networks.
/// </summary>
internal static class SafeHttp
{
    public static HttpClient CreateClient(bool allowPrivateNetworks, IReadOnlyCollection<string>? allowedHosts, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, cancellationToken) =>
            {
                string host = context.DnsEndPoint.Host;
                if (allowedHosts is { Count: > 0 } && !allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                    throw new HttpRequestException($"Host '{host}' is not in the list of allowed hosts.");
                var addresses = IPAddress.TryParse(host, out var literal)
                    ? new[] { literal }
                    : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                var allowed = addresses.Where(a => allowPrivateNetworks || IsPublic(a)).ToArray();
                if (allowed.Length == 0)
                    throw new HttpRequestException($"Host '{host}' resolves to a private or reserved address; access is blocked (AllowPrivateNetworks).");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>True for globally routable unicast addresses.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast) || address.Equals(IPAddress.IPv6None))
            return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224 ||   // this network, private, loopback, multicast/reserved
                     (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||              // carrier-grade NAT
                     (b[0] == 169 && b[1] == 254) ||                            // link-local (cloud metadata services)
                     (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                     (b[0] == 192 && b[1] == 168) ||
                     (b[0] == 192 && b[1] == 0 && b[2] == 0) ||
                     (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                     (b[0] & 0xFE) == 0xFC ||                                   // unique local fc00::/7
                     (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8));
        }
        return false;
    }
}
