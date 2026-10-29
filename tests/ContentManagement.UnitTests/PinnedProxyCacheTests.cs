using ContentManagement.Server.Services;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace ContentManagement.UnitTests;

public sealed class PinnedProxyCacheTests
{
    [Fact]
    public void Set_ReplacesPreviouslyPinnedProxy_WithoutExpiration()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var pinnedCache = new PinnedProxyCache(memoryCache);
        var first = new PinnedProxySnapshot("http://8.8.8.8:8080", "http", DateTime.UtcNow, null, null, null);
        var second = new PinnedProxySnapshot("socks5://1.1.1.1:1080", "socks5", DateTime.UtcNow.AddSeconds(1), null, null, null);

        pinnedCache.Set(first);
        Assert.Equal(first, pinnedCache.Get());

        pinnedCache.Set(second);

        Assert.Equal(second, pinnedCache.Get());
    }

    [Fact]
    public void Clear_RemovesCachedPinnedProxy()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var pinnedCache = new PinnedProxyCache(memoryCache);
        pinnedCache.Set(new PinnedProxySnapshot("http://8.8.8.8:8080", "http", DateTime.UtcNow, null, null, null));

        pinnedCache.Clear();

        Assert.Null(pinnedCache.Get());
    }
}
