using System.Text.Json;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using ContentManagement.Server.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.BackgroundJobs;

public sealed class PinnedProxyHealthJob(
    ContentManagementDbContext db,
    ProxyDirectoryClient proxyDirectory,
    ProxyHealthService healthService,
    ProxyPool proxyPool,
    PinnedProxyCache pinnedCache,
    ILogger<PinnedProxyHealthJob> logger)
{
    private const int SingletonId = 1;
    private const int MaximumConcurrentProbes = 5;

    [DisableConcurrentExecution(timeoutInSeconds: 240)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // The off-schedule trigger and the recurring cron share this gate so a burst of failing
        // relay requests cannot start overlapping failovers.
        if (!proxyPool.TryBeginRefresh()) return;
        try
        {
            await ExecuteCoreAsync(cancellationToken);
        }
        finally
        {
            proxyPool.EndRefresh();
        }
    }

    private async Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        var pinned = await db.PinnedProxies.SingleOrDefaultAsync(item => item.Id == SingletonId, cancellationToken);
        if (pinned is null) return;

        var settings = await db.SystemSettings.AsNoTracking().SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        var candidates = await FetchCandidatesAsync(cancellationToken);
        await RefreshPoolAsync(candidates, pinned.Address, cancellationToken);

        var currentHealth = await healthService.CheckAsync(pinned.Address, cancellationToken);
        proxyPool.RecordProbe(pinned.Address, pinned.Protocol, currentHealth);
        pinned.LastCheckedUtc = DateTime.UtcNow;
        pinned.LatencyMilliseconds = currentHealth.LatencyMilliseconds;
        pinned.IsHealthy = currentHealth.IsHealthy;
        await db.SaveChangesAsync(cancellationToken);

        var swapIntervalMinutes = settings?.ProxySwapIntervalMinutes ?? 60;
        var swapDue = settings?.ProxySwapEnabled == true &&
                      DateTime.UtcNow - pinned.PinnedUtc >= TimeSpan.FromMinutes(swapIntervalMinutes);
        var incumbent = proxyPool.Find(pinned.Address);
        var best = proxyPool.Best(excludeAddress: pinned.Address);

        var isHealthy = currentHealth.IsHealthy;
        if (isHealthy && !swapDue && !proxyPool.IsSignificantlyBetter(best, incumbent))
        {
            pinnedCache.Set(ToSnapshot(pinned));
            logger.LogInformation(
                "Pinned proxy {ProxyAddress} relays traffic in {LatencyMs} ms. Automatic swap enabled: {SwapEnabled}; interval: {SwapIntervalMinutes} minutes; due: {SwapDue}; pool size: {PoolSize}.",
                pinned.Address, currentHealth.LatencyMilliseconds, settings?.ProxySwapEnabled == true,
                swapIntervalMinutes, swapDue, proxyPool.Snapshot().Count);
            return;
        }

        if (!isHealthy)
            logger.LogWarning("Pinned proxy {ProxyAddress} did not relay the health probe; searching for a replacement immediately.", pinned.Address);
        else if (swapDue)
            logger.LogInformation("Pinned proxy {ProxyAddress} reached its configured swap interval of {SwapIntervalMinutes} minutes; rotating proxy.",
                pinned.Address, swapIntervalMinutes);
        else
            logger.LogInformation("A verified proxy scored better than pinned proxy {ProxyAddress}; rotating proxy.", pinned.Address);

        if (best is null)
        {
            pinnedCache.Set(ToSnapshot(pinned));
            logger.LogError("No verified replacement proxy was found; retaining the current pin with unhealthy status.");
            return;
        }

        pinned.Address = best.Address;
        pinned.Protocol = best.Protocol;
        pinned.PinnedUtc = DateTime.UtcNow;
        pinned.LastCheckedUtc = pinned.PinnedUtc;
        pinned.LatencyMilliseconds = best.MedianLatencyMilliseconds;
        pinned.IsHealthy = true;
        await db.SaveChangesAsync(cancellationToken);
        pinnedCache.Set(ToSnapshot(pinned));
        logger.LogInformation("Pinned proxy automatically changed to {ProxyAddress} (score {Score}, {LatencyMs} ms).",
            pinned.Address, best.Score, best.MedianLatencyMilliseconds);
    }

    private async Task RefreshPoolAsync(
        IReadOnlyList<(string Address, string Protocol)> candidates, string pinnedAddress,
        CancellationToken cancellationToken)
    {
        var targets = proxyPool.SelectProbeTargets(candidates, DateTimeOffset.UtcNow);
        if (targets.Count == 0) return;

        using var semaphore = new SemaphoreSlim(MaximumConcurrentProbes);
        var probes = targets.Select(async target =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var result = await healthService.CheckAsync(target.Address, cancellationToken);
                proxyPool.RecordProbe(target.Address, target.Protocol, result);
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(probes);
        proxyPool.Prune(pinnedAddress);
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