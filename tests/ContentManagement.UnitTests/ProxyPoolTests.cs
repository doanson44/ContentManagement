using ContentManagement.Server.Configuration;
using ContentManagement.Server.Services;
using Xunit;

namespace ContentManagement.UnitTests;

public sealed class ProxyPoolTests
{
    [Fact]
    public void Best_RequiresAVerifiedRelay()
    {
        var pool = CreatePool();
        pool.RecordRelay("http://8.8.8.8:8080", "http", succeeded: true, elapsedMilliseconds: 200);

        Assert.Null(pool.Best());

        pool.RecordProbe("http://8.8.8.8:8080", "http", new ProxyHealthResult(true, 200));

        var best = pool.Best();
        Assert.NotNull(best);
        Assert.True(best!.Verified);
        Assert.Equal(0, best.ConsecutiveFailures);
        Assert.True(best.Score > 0);
    }

    [Fact]
    public void Best_ExcludesRequestedAddress()
    {
        var pool = CreatePool();
        pool.RecordProbe("http://8.8.8.8:8080", "http", new ProxyHealthResult(true, 100));
        pool.RecordProbe("socks5://9.9.9.9:1080", "socks5", new ProxyHealthResult(true, 2000));

        var best = pool.Best(excludeAddress: "http://8.8.8.8:8080");

        Assert.NotNull(best);
        Assert.Equal("socks5://9.9.9.9:1080", best!.Address);
    }

    [Fact]
    public void Best_PrefersFasterVerifiedProxy()
    {
        var pool = CreatePool();
        pool.RecordProbe("http://8.8.8.8:8080", "http", new ProxyHealthResult(true, 3000));
        pool.RecordProbe("socks5://9.9.9.9:1080", "socks5", new ProxyHealthResult(true, 150));

        Assert.Equal("socks5://9.9.9.9:1080", pool.Best()!.Address);
    }

    [Fact]
    public void Best_ExcludesProxiesAboveTheLatencyBudget()
    {
        var pool = CreatePool(new ProxyHealthOptions { MaximumRelayLatencyMilliseconds = 2000 });
        pool.RecordProbe("http://8.8.8.8:8080", "http", new ProxyHealthResult(true, 5000));

        Assert.Null(pool.Best());
    }

    [Fact]
    public void RecordFailure_MarksProxySuspectAfterThreshold()
    {
        var pool = CreatePool();
        pool.RecordProbe("http://8.8.8.8:8080", "http", new ProxyHealthResult(true, 200));

        pool.RecordRelay("http://8.8.8.8:8080", "http", succeeded: false, elapsedMilliseconds: null);
        Assert.False(pool.IsSuspect("http://8.8.8.8:8080"));

        pool.RecordRelay("http://8.8.8.8:8080", "http", succeeded: false, elapsedMilliseconds: null);
        Assert.True(pool.IsSuspect("http://8.8.8.8:8080"));
        Assert.Null(pool.Best());
    }

    [Fact]
    public void RecordSuccess_ClearsSuspectState()
    {
        var pool = CreatePool();
        pool.RecordProbe("http://8.8.8.8:8080", "http", new ProxyHealthResult(true, 200));
        pool.RecordRelay("http://8.8.8.8:8080", "http", succeeded: false, elapsedMilliseconds: null);
        pool.RecordRelay("http://8.8.8.8:8080", "http", succeeded: false, elapsedMilliseconds: null);

        pool.RecordRelay("http://8.8.8.8:8080", "http", succeeded: true, elapsedMilliseconds: 300);

        Assert.False(pool.IsSuspect("http://8.8.8.8:8080"));
        Assert.NotNull(pool.Best());
    }

    [Fact]
    public void IsSignificantlyBetter_RequiresTheConfiguredMargin()
    {
        var pool = CreatePool();
        pool.RecordProbe("http://8.8.8.8:8080", "http", new ProxyHealthResult(true, 1000));
        pool.RecordProbe("socks5://9.9.9.9:1080", "socks5", new ProxyHealthResult(true, 1000));

        var incumbent = pool.Find("http://8.8.8.8:8080");
        var equal = pool.Find("socks5://9.9.9.9:1080");
        Assert.False(pool.IsSignificantlyBetter(equal, incumbent));

        pool.RecordProbe("socks5://9.9.9.9:1080", "socks5", new ProxyHealthResult(true, 100));
        var faster = pool.Find("socks5://9.9.9.9:1080");
        Assert.True(pool.IsSignificantlyBetter(faster, incumbent));

        Assert.False(pool.IsSignificantlyBetter(null, incumbent));
        Assert.True(pool.IsSignificantlyBetter(faster, null));
    }

    [Fact]
    public void SelectProbeTargets_SkipsFreshEntriesAndDeduplicatesHosts()
    {
        var pool = CreatePool();
        pool.RecordProbe("http://8.8.8.8:8080", "http", new ProxyHealthResult(true, 200));

        var targets = pool.SelectProbeTargets(
        [
            ("http://8.8.8.8:8080", "http"),
            ("http://1.1.1.1:8080", "http"),
            ("http://1.1.1.1:3128", "http")
        ], DateTimeOffset.UtcNow);

        Assert.DoesNotContain(targets, target => target.Address == "http://8.8.8.8:8080");
        Assert.Single(targets);
        Assert.Equal("http://1.1.1.1:8080", targets[0].Address);
    }

    [Fact]
    public void SelectProbeTargets_RespectsThePerRunAndPoolLimits()
    {
        var pool = CreatePool();
        var candidates = Enumerable.Range(1, 40)
            .Select(index => ($"http://11.0.0.{index}:8080", "http"))
            .ToArray();

        var targets = pool.SelectProbeTargets(candidates, DateTimeOffset.UtcNow);

        Assert.True(targets.Count <= new ProxyHealthOptions().MaxProbesPerRun);
        Assert.True(targets.Count <= new ProxyHealthOptions().PoolSize);
    }

    [Fact]
    public void TryRequestRepin_EnforcesCooldown()
    {
        var pool = CreatePool();

        Assert.True(pool.TryRequestRepin());
        Assert.False(pool.TryRequestRepin());
    }

    private static ProxyPool CreatePool(ProxyHealthOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? new ProxyHealthOptions()));
}
