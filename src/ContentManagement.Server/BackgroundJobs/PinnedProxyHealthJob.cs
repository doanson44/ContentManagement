using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using ContentManagement.Server.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.BackgroundJobs;

public sealed class PinnedProxyHealthJob(
    ContentManagementDbContext db,
    IHttpClientFactory httpClientFactory,
    ProxyHealthService healthService,
    PinnedProxyCache pinnedCache,
    ILogger<PinnedProxyHealthJob> logger)
{
    private const int SingletonId = 1;
    private const int CandidateLimit = 30;
    private const long MaximumHealthyLatencyMs = 3000;
    private const string Upstream = "https://api.proxyscrape.com/v4/free-proxy-list/get?request=display_proxies&proxy_format=protocolipport&format=json&country=vn";

    [DisableConcurrentExecution(timeoutInSeconds: 240)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var pinned = await db.PinnedProxies.SingleOrDefaultAsync(item => item.Id == SingletonId, cancellationToken);
        if (pinned is null) return;

        var currentHealth = await healthService.CheckAsync(pinned.Address, cancellationToken);
        var checkedAt = DateTime.UtcNow;
        pinned.LastCheckedUtc = checkedAt;
        pinned.LatencyMilliseconds = currentHealth.LatencyMilliseconds;
        pinned.IsHealthy = currentHealth.IsHealthy;
        await db.SaveChangesAsync(cancellationToken);

        if (currentHealth.IsHealthy && currentHealth.LatencyMilliseconds <= MaximumHealthyLatencyMs)
        {
            pinnedCache.Set(ToSnapshot(pinned));
            logger.LogInformation("Pinned proxy {ProxyAddress} is healthy at {LatencyMs} ms.",
                pinned.Address, currentHealth.LatencyMilliseconds);
            return;
        }

        logger.LogWarning("Pinned proxy {ProxyAddress} is unhealthy or slow; searching for a replacement.", pinned.Address);
        var candidates = await FetchCandidatesAsync(cancellationToken);
        var ranked = new List<(string Address, string Protocol, long Latency)>();
        using var semaphore = new SemaphoreSlim(5);
        var checks = candidates.Where(candidate => !candidate.Address.Equals(pinned.Address, StringComparison.OrdinalIgnoreCase))
            .Take(CandidateLimit)
            .Select(async candidate =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var result = await healthService.CheckAsync(candidate.Address, cancellationToken);
                    if (result.IsHealthy && result.LatencyMilliseconds is <= MaximumHealthyLatencyMs)
                        lock (ranked) ranked.Add((candidate.Address, candidate.Protocol, result.LatencyMilliseconds.Value));
                }
                finally { semaphore.Release(); }
            });
        await Task.WhenAll(checks);

        var best = ranked.OrderBy(item => item.Latency).FirstOrDefault();
        if (best.Address is null)
        {
            pinnedCache.Set(ToSnapshot(pinned));
            logger.LogError("No healthy replacement proxy was found; retaining the current pin with unhealthy status.");
            return;
        }

        pinned.Address = best.Address;
        pinned.Protocol = best.Protocol;
        pinned.PinnedUtc = DateTime.UtcNow;
        pinned.LastCheckedUtc = pinned.PinnedUtc;
        pinned.LatencyMilliseconds = best.Latency;
        pinned.IsHealthy = true;
        await db.SaveChangesAsync(cancellationToken);
        pinnedCache.Set(ToSnapshot(pinned));
        logger.LogInformation("Pinned proxy automatically changed to {ProxyAddress} at {LatencyMs} ms.",
            pinned.Address, best.Latency);
    }

    private async Task<List<(string Address, string Protocol)>> FetchCandidatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClientFactory.CreateClient("ProxyScrape").GetAsync(Upstream, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var result = new Dictionary<string, (string Address, string Protocol)>(StringComparer.OrdinalIgnoreCase);
            Extract(document.RootElement, result);
            return result.Values.ToList();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Unable to fetch proxy candidates for automatic failover.");
            return [];
        }
    }

    private static void Extract(JsonElement element, IDictionary<string, (string Address, string Protocol)> result)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            Add(element.GetString(), result);
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) Extract(child, result);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        var protocol = ReadString(element, "protocol", "scheme", "type");
        var host = ReadString(element, "ip", "host", "hostname");
        var port = ReadPort(element, "port");
        if (!string.IsNullOrWhiteSpace(protocol) && !string.IsNullOrWhiteSpace(host) &&
            port is > 0 and <= 65535)
            Add($"{protocol}://{host}:{port}", result);
        foreach (var property in element.EnumerateObject()) Extract(property.Value, result);
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static int? ReadPort(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var port)) return port;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static void Add(string? candidate, IDictionary<string, (string Address, string Protocol)> result)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Port is < 1 or > 65535 || uri.Scheme is not ("http" or "https" or "socks4" or "socks5") ||
            !IPAddress.TryParse(uri.Host, out _)) return;
        var protocol = uri.Scheme.ToLowerInvariant();
        var address = $"{protocol}://{uri.Host}:{uri.Port}";
        result.TryAdd(address, (address, protocol));
    }

    private static PinnedProxySnapshot ToSnapshot(PinnedProxy proxy) =>
        new(proxy.Address, proxy.Protocol, proxy.PinnedUtc, proxy.LastCheckedUtc, proxy.LatencyMilliseconds, proxy.IsHealthy);
}