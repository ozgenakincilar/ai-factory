using System.Net;
using System.Net.Sockets;

namespace Factory.Infrastructure.Security;

// Egress policy for customer model endpoints (SSRF defense). URLs must be absolute HTTPS without
// credentials or fragments. Every connection re-resolves the host and connects only to an allowed
// address, so DNS rebinding cannot reach Factory or private networks. Redirects and proxies are
// disabled. Loopback (HTTP or HTTPS) is allowed only when explicitly enabled for Development/Testing.
// Private-network endpoints need a separately designed and approved mechanism.
public sealed class ModelEndpointPolicy(bool allowLoopback)
{
    public const string Setting = "ModelEndpoints:AllowLoopbackForDevelopment";

    public static ModelEndpointPolicy FromConfiguration(bool allowLoopback, bool developmentOrTesting)
        => allowLoopback && !developmentOrTesting
            ? throw new InvalidOperationException("loopback_model_endpoints_forbidden_outside_development")
            : new ModelEndpointPolicy(allowLoopback);

    public bool AllowLoopback => allowLoopback;

    public Uri ValidateUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Length > 500 || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query))
            throw new ArgumentException("invalid_model_endpoint");
        var loopbackHost = uri.IsLoopback;
        if (uri.Scheme == Uri.UriSchemeHttps && (!loopbackHost || allowLoopback)) return uri;
        if (uri.Scheme == Uri.UriSchemeHttp && loopbackHost && allowLoopback) return uri;
        throw new ArgumentException("invalid_model_endpoint");
    }

    public bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return allowLoopback;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) || b[0] >= 224);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false; // fc00::/7 unique local
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) // 64:ff9b::/96 NAT64 embeds IPv4
                return IsAllowed(new IPAddress(b[12..16]));
            return true;
        }
        return false;
    }

    // One client per endpoint; callers own disposal.
    public HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                // Strict: any forbidden address in the answer refuses the connection.
                if (addresses.Length == 0 || !addresses.All(IsAllowed)) throw new HttpRequestException("model_endpoint_address_forbidden");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    }
}
