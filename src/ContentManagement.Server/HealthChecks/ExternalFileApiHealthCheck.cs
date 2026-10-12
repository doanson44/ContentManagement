using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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

            var client = httpClientFactory.CreateClient(nameof(ExternalFileApiHealthCheck));
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(baseUri.ToString().TrimEnd('/') + "/health"));
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Unhealthy(
                    $"External File API health endpoint returned HTTP {(int)response.StatusCode}.");
            }

            // Also validate that the configured API key can authenticate against a protected endpoint.
            var key = _protector.Unprotect(settings.ExternalStorageApiKeyProtected);
            using var authRequest = new HttpRequestMessage(HttpMethod.Get,
                new Uri(baseUri.ToString().TrimEnd('/') + "/api/files/inventory?olderThanUtc=" +
                    Uri.EscapeDataString(DateTime.UtcNow.AddDays(-36500).ToString("O"))));
            authRequest.Headers.Add("X-API-Key", key);

            using var authResponse = await client.SendAsync(
                authRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (authResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return HealthCheckResult.Unhealthy("External File API is reachable, but API key authentication failed.");

            if (!authResponse.IsSuccessStatusCode)
                return HealthCheckResult.Unhealthy(
                    $"External File API authenticated check returned HTTP {(int)authResponse.StatusCode}.");

            return HealthCheckResult.Healthy("External File API is reachable and API key authentication succeeded.");
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
