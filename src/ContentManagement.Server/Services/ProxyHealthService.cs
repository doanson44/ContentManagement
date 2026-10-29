using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ContentManagement.Server.Configuration;
using Microsoft.Extensions.Options;

namespace ContentManagement.Server.Services;

/// <summary>
/// Verifies that a proxy actually relays an HTTP request to the configured probe URL.
/// A reachable TCP port is not sufficient: free public proxies frequently accept
/// connections without forwarding traffic, which produced unusable pins and failovers.
/// The probe target must stay public and credential-free because the proxy observes it.
/// </summary>
public sealed class ProxyHealthService(IOptions<ProxyHealthOptions> options, ILogger<ProxyHealthService> logger)
{
    public async Task<ProxyHealthResult> CheckAsync(string address, CancellationToken cancellationToken)
    {
        if (!ProxyAddressRules.TryNormalize(address, out var normalized, out _))
            return new ProxyHealthResult(false, null);

        var proxyUri = new Uri(normalized);
        if (!IPAddress.TryParse(proxyUri.Host, out var proxyIp) || !ProxyAddressRules.IsPublicAddress(proxyIp))
            return new ProxyHealthResult(false, null);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ProbeTimeoutSeconds));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var handler = ProxyRelay.CreateHandler(proxyUri);
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, options.Value.ProbeUri);
            request.Headers.ConnectionClose = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            stopwatch.Stop();
            return new ProxyHealthResult(true, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is SocketException or HttpRequestException or IOException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            logger.LogDebug("Proxy relay probe failed for {ProxyAddress}: {FailureType}.",
                address, exception.GetType().Name);
            return new ProxyHealthResult(false, null);
        }
    }
}

public sealed record ProxyHealthResult(bool IsHealthy, long? LatencyMilliseconds);