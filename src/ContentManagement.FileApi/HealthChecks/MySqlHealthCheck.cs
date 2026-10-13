using Microsoft.Extensions.Diagnostics.HealthChecks;
using MySqlConnector;

namespace ContentManagement.FileApi.HealthChecks;

public sealed class MySqlHealthCheck(IConfiguration configuration, ILogger<MySqlHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("MySql");
        if (string.IsNullOrWhiteSpace(connectionString))
            return HealthCheckResult.Unhealthy("ConnectionStrings:MySql is not configured.");

        try
        {
            var settings = new MySqlConnectionStringBuilder(connectionString)
            {
                ConnectionTimeout = 5,
                DefaultCommandTimeout = 5
            };
            await using var connection = new MySqlConnection(settings.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 5;
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1
                ? HealthCheckResult.Healthy("MySQL connection and test query succeeded.")
                : HealthCheckResult.Unhealthy("MySQL health query returned an unexpected result.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("MySQL health check timed out.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "MySQL health check failed.");
            return HealthCheckResult.Unhealthy("MySQL health check failed.");
        }
    }
}
