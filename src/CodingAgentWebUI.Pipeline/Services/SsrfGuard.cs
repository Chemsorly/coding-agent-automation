using System.Net;
using System.Net.Sockets;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Prevents Server-Side Request Forgery (SSRF) by validating resolved IP addresses before
/// allowing outbound connections. Blocks RFC 1918 private ranges, link-local, loopback,
/// and cloud metadata endpoints.
/// </summary>
public sealed class SsrfGuard
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _dnsResolver;

    public SsrfGuard(Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null)
    {
        _dnsResolver = dnsResolver ?? Dns.GetHostAddressesAsync;
    }

    /// <summary>
    /// Custom connect callback for <see cref="SocketsHttpHandler.ConnectCallback"/>.
    /// Resolves DNS, filters blocked IPs, and connects to the first allowed address.
    /// </summary>
    public async ValueTask<Stream> ConnectCallbackAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        var addresses = await _dnsResolver(host, ct).ConfigureAwait(false);

        if (addresses.Length == 0)
            throw new HttpRequestException($"DNS resolution returned no addresses for '{host}'.");

        foreach (var address in addresses)
        {
            if (IsBlocked(address))
                continue;

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException(
            $"All resolved addresses for '{host}' are blocked by SSRF policy.");
    }

    /// <summary>
    /// Returns <c>true</c> if the address falls within a blocked range:
    /// RFC 1918 private, link-local, loopback, or cloud metadata (169.254.169.254).
    /// </summary>
    public static bool IsBlocked(IPAddress address)
    {
        // Normalize IPv4-mapped IPv6 (e.g., ::ffff:127.0.0.1) to IPv4
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            // 10.0.0.0/8
            if (bytes[0] == 10)
                return true;

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;

            // 169.254.0.0/16 (link-local, includes cloud metadata 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fe80::/10 (IPv6 link-local)
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Creates a pre-configured <see cref="SocketsHttpHandler"/> with SSRF protection enabled.
    /// Auto-redirect is disabled (manual redirect handling expected by caller).
    /// Automatic decompression is disabled (manual gzip handling with byte counting).
    /// </summary>
    public static SocketsHttpHandler CreateHandler(
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null)
    {
        var guard = new SsrfGuard(dnsResolver);
        return new SocketsHttpHandler
        {
            ConnectCallback = guard.ConnectCallbackAsync,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
    }
}
