using ContentManagement.Server.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ContentManagement.UnitTests;

public sealed class HealthControllerTests
{
    [Theory]
    [InlineData(HealthStatus.Healthy, 200)]
    [InlineData(HealthStatus.Degraded, 503)]
    [InlineData(HealthStatus.Unhealthy, 503)]
    public async Task Get_MapsHealthStatusToHttpStatus(HealthStatus healthStatus, int expectedStatusCode)
    {
        var healthCheckService = new StubHealthCheckService(healthStatus);
        var controller = new HealthController(healthCheckService);

        var result = await controller.Get(CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Equal(healthStatus.ToString(), Assert.IsType<HealthResponse>(response.Value).Status);
    }

    private sealed class StubHealthCheckService(HealthStatus status) : HealthCheckService
    {
        public override Task<HealthReport> CheckHealthAsync(
            Func<HealthCheckRegistration, bool>? predicate,
            CancellationToken cancellationToken = default)
        {
            var report = new HealthReport(
                new Dictionary<string, HealthReportEntry>(),
                status,
                TimeSpan.Zero);

            return Task.FromResult(report);
        }
    }
}
