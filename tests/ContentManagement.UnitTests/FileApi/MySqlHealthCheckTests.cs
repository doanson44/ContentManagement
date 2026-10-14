using ContentManagement.FileApi.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ContentManagement.UnitTests.FileApi;

public sealed class MySqlHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_WithoutConnectionString_ReturnsHealthy()
    {
        var check = new MySqlHealthCheck(new ConfigurationBuilder().Build(), NullLogger<MySqlHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_WithUnusableConnectionString_ReturnsUnhealthy()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MySql"] = "Server=127.0.0.1;Port=1;Uid=nobody;Pwd=invalid;Pooling=false"
            })
            .Build();
        var check = new MySqlHealthCheck(configuration, NullLogger<MySqlHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
