using Microsoft.Extensions.Caching.Memory;

namespace ContentManagement.Server.Services;

public sealed class PinnedProxyCache(IMemoryCache cache)
{
    public const string CacheKey = "proxy:pinned:current";

    public PinnedProxySnapshot? Get() =>
        cache.TryGetValue(CacheKey, out PinnedProxySnapshot? value) ? value : null;

    public void Set(PinnedProxySnapshot value) =>
        cache.Set(CacheKey, value); // No expiration: replaced only when another proxy is pinned.

    public void Clear() => cache.Remove(CacheKey);
}

public sealed record PinnedProxySnapshot(
    string Address,
    string Protocol,
    DateTime PinnedUtc,
    DateTime? LastCheckedUtc,
    long? LatencyMilliseconds,
    bool? IsHealthy);