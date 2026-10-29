using ContentManagement.Server.BackgroundJobs;

namespace ContentManagement.Server.Services;

/// <summary>
/// Runs the health/failover job off-schedule when relay traffic proves the pinned proxy is
/// failing. The pool enforces a short cooldown and a single-flight refresh so a burst of
/// failing requests cannot start overlapping failovers.
/// </summary>
public sealed class ProxyRepinTrigger(
    IServiceScopeFactory scopeFactory,
    ProxyPool proxyPool,
    ILogger<ProxyRepinTrigger> logger)
{
    private int _running;

    public void TryRequest()
    {
        if (!proxyPool.TryRequestRepin()) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<PinnedProxyHealthJob>();
            await job.ExecuteAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Off-schedule proxy re-pin failed.");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
