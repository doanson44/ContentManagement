using ContentManagement.Server.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ContentManagement.Server.HealthChecks;

public sealed class ExternalFileApiHealthCheck(
    IHttpClientFactory httpClientFactory,
    ContentManagementDbContext db,
    IDataProtectionProvider protectionProvider,
    ILogger<ExternalFileApiHealthCheck> logger) : IHealthCheck
{
    private readonly IDataProtector _protector =
        protectionProvider.CreateProtector("ContentManagement.ExternalFileApi.ApiKey.v1");

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await db.SystemSettings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);

            if (settings is null ||
                string.IsNullOrWhiteSpace(settings.ExternalStorageBaseUrl) ||
                string.IsNullOrWhiteSpace(settings.ExternalStorageApiKeyProtected))
            {
                return HealthCheckResult.Healthy("External File API is not configured.");
            }

            if (!Uri.TryCreate(settings.ExternalStorageBaseUrl, UriKind.Absolute, out var baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttps &&
                 !(baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback)))
            {
                return HealthCheckResult.Unhealthy("External File API URL is invalid or does not use HTTPS.");
            }

            var key = _protector.Unprotect(settings.ExternalStorageApiKeyProtected);
            var endpoint = new Uri(baseUri.ToString().TrimEnd('/') + "/api/files/health");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Add("X-API-Key", key);

            var client = httpClientFactory.CreateClient(nameof(ExternalFileApiHealthCheck));
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Unhealthy(
                    $"External File API health check returned HTTP {(int)response.StatusCode}.");
            }

            return HealthCheckResult.Healthy(
                "External File API is reachable and API key authentication succeeded.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("External File API health check timed out.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "External File API health check failed.");
            return HealthCheckResult.Unhealthy("External File API health check failed.");
        }
    }
}
