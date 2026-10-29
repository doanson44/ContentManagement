using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ContentManagement.Server.Services;

/// <summary>
/// Measures TCP reachability and connection latency only. This deliberately avoids
/// sending application data or credentials through untrusted public proxies.
/// </summary>
public sealed class ProxyHealthService(ILogger<ProxyHealthService> logger)
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);

    public async Task<ProxyHealthResult> CheckAsync(string address, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks4" or "socks5") ||
            !IPAddress.TryParse(uri.Host, out _) ||
            uri.Port is < 1 or > 65535)
            return new ProxyHealthResult(false, null);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, uri.Port, timeout.Token);
            stopwatch.Stop();
            return new ProxyHealthResult(true, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or IOException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            logger.LogDebug("Proxy TCP connectivity check failed for {ProxyAddress}: {FailureType}.",
                address, exception.GetType().Name);
            return new ProxyHealthResult(false, null);
        }
    }

    private static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal)
            return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] != 0 && bytes[0] != 10 && bytes[0] != 127 &&
                !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && bytes[1] == 168) &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) && bytes[0] < 224;
        }
        var bytes6 = ip.GetAddressBytes();
        return !ip.Equals(IPAddress.IPv6Any) && !(bytes6[0] == 0xFC || bytes6[0] == 0xFD);
    }
}

public sealed record ProxyHealthResult(bool IsHealthy, long? LatencyMilliseconds);