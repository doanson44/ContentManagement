using ContentManagement.Server.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ContentManagement.Server.HealthChecks;

public sealed class ExternalFileApiHealthCheck(
    IHttpClientFactory httpClientFactory,
    IOptions<ExternalStorageOptions> externalStorageOptions,
    ILogger<ExternalFileApiHealthCheck> logger) : IHealthCheck
{
    private readonly ExternalStorageOptions _options = externalStorageOptions.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
            return HealthCheckResult.Healthy("External File API is not configured.");

        try
        {
            if (!Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
            {
                return HealthCheckResult.Unhealthy("External File API URL must be an absolute HTTP or HTTPS URL.");
            }

            var endpoint = new Uri(baseUri.ToString().TrimEnd('/') + "/api/files/health");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Add("X-API-Key", _options.ApiKey);

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
