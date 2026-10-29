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
}

public sealed record ProxyHealthResult(bool IsHealthy, long? LatencyMilliseconds);