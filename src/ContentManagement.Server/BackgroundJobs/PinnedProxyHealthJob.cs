using System.Text.Json;
using ContentManagement.Server.Configuration;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using ContentManagement.Server.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ContentManagement.Server.BackgroundJobs;

public sealed class PinnedProxyHealthJob(
    ContentManagementDbContext db,
    ProxyDirectoryClient proxyDirectory,
    ProxyHealthService healthService,
    IOptions<ProxyHealthOptions> healthOptions,
    PinnedProxyCache pinnedCache,
    ILogger<PinnedProxyHealthJob> logger)
{
    private const int SingletonId = 1;
    private const int CandidateLimit = 30;

    [DisableConcurrentExecution(timeoutInSeconds: 240)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var pinned = await db.PinnedProxies.SingleOrDefaultAsync(item => item.Id == SingletonId, cancellationToken);
        if (pinned is null) return;

        var settings = await db.SystemSettings.AsNoTracking().SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        var currentHealth = await healthService.CheckAsync(pinned.Address, cancellationToken);
        var checkedAt = DateTime.UtcNow;
        pinned.LastCheckedUtc = checkedAt;
        pinned.LatencyMilliseconds = currentHealth.LatencyMilliseconds;
        pinned.IsHealthy = currentHealth.IsHealthy;
        await db.SaveChangesAsync(cancellationToken);

        // A relay that works but is slower than the candidate threshold stays pinned; the
        // threshold only ranks and qualifies replacement candidates when a swap is required.
        var isHealthy = currentHealth.IsHealthy;
        var swapIntervalMinutes = settings?.ProxySwapIntervalMinutes ?? 60;
        var swapDue = settings?.ProxySwapEnabled == true &&
                      DateTime.UtcNow - pinned.PinnedUtc >= TimeSpan.FromMinutes(swapIntervalMinutes);

        if (isHealthy && !swapDue)
        {
            pinnedCache.Set(ToSnapshot(pinned));
            logger.LogInformation(
                "Pinned proxy {ProxyAddress} is healthy at {LatencyMs} ms. Automatic swap enabled: {SwapEnabled}; interval: {SwapIntervalMinutes} minutes; due: {SwapDue}.",
                pinned.Address, currentHealth.LatencyMilliseconds, settings?.ProxySwapEnabled == true,
                swapIntervalMinutes, swapDue);
            return;
        }

        if (!isHealthy)
            logger.LogWarning("Pinned proxy {ProxyAddress} did not relay the health probe; searching for a replacement immediately.", pinned.Address);
        else
            logger.LogInformation("Pinned proxy {ProxyAddress} reached its configured swap interval of {SwapIntervalMinutes} minutes; rotating proxy.",
                pinned.Address, swapIntervalMinutes);
        var candidates = await FetchCandidatesAsync(cancellationToken);
        var maximumLatencyMs = healthOptions.Value.MaximumRelayLatencyMilliseconds;
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
                    if (result.IsHealthy && result.LatencyMilliseconds <= maximumLatencyMs)
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
            var directory = await proxyDirectory.GetAsync(cancellationToken);
            return directory.Proxies.Select(proxy => (proxy.Address, proxy.Protocol)).ToList();
        }
        catch (Exception exception) when ((exception is HttpRequestException or JsonException or ProxyDirectoryException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Unable to fetch proxy candidates for automatic failover.");
            return [];
        }
    }

    private static PinnedProxySnapshot ToSnapshot(PinnedProxy proxy) =>
        new(proxy.Address, proxy.Protocol, proxy.PinnedUtc, proxy.LastCheckedUtc, proxy.LatencyMilliseconds, proxy.IsHealthy);
}