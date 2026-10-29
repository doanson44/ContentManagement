using ContentManagement.Server.Configuration;
using ContentManagement.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ContentManagement.UnitTests;

public sealed class ProxyHealthServiceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not-a-proxy")]
    [InlineData("http://proxy.example.com:8080")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://10.0.0.5:3128")]
    [InlineData("socks5://192.168.1.10:1080")]
    public async Task CheckAsync_ReportsUnhealthy_ForUnsupportedOrNonPublicEndpoints(string address)
    {
        var service = CreateService();

        var result = await service.CheckAsync(address, CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.Null(result.LatencyMilliseconds);
    }

    [Fact]
    public void ProxyHealthOptions_DefaultToACredentialFreeHttpsProbe()
    {
        var options = new ProxyHealthOptions();

        Assert.Equal("https", options.ProbeUri.Scheme);
        Assert.InRange(options.ProbeTimeoutSeconds, 3, 30);
        Assert.InRange(options.MaximumRelayLatencyMilliseconds, 1000, 60000);
    }

    private static ProxyHealthService CreateService() =>
        new(Options.Create(new ProxyHealthOptions()), NullLogger<ProxyHealthService>.Instance);
}
