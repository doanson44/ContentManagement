using IBM.Data.Db2;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ContentManagement.FileApi.HealthChecks;

public sealed class Db2HealthCheck(
    IConfiguration configuration,
    ILogger<Db2HealthCheck> logger) : IHealthCheck
{
    private const string ConnectionStringName = "Db2";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return HealthCheckResult.Unhealthy(
                $"ConnectionStrings:{ConnectionStringName} is not configured.");
        }

        try
        {
            await using var connection = new DB2Connection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM SYSIBM.SYSDUMMY1";
            command.CommandTimeout = 5;
            var result = await command.ExecuteScalarAsync(cancellationToken);

            return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1
                ? HealthCheckResult.Healthy("DB2 connection and test query succeeded.")
                : HealthCheckResult.Unhealthy("DB2 health query returned an unexpected result.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("DB2 health check timed out.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "DB2 health check failed.");
            return HealthCheckResult.Unhealthy("DB2 health check failed.");
        }
    }
}
