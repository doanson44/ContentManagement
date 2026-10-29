using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/proxies")]
[Authorize(Roles = "Administrator")]
public sealed class ProxiesController(
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    ILogger<ProxiesController> logger) : ControllerBase
{
    private const string CacheKey = "proxyscrape:vietnam:protocolipport";
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly Uri ProxyScrapeUri = new(
        "https://api.proxyscrape.com/v4/free-proxy-list/get?request=display_proxies&proxy_format=protocolipport&format=json&country=vn");

    [HttpGet]
    [ProducesResponseType<ProxyListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<ProxyListResponse>> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await memoryCache.GetOrCreateAsync(CacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
                var client = httpClientFactory.CreateClient("ProxyScrape");
                using var response = await client.GetAsync(ProxyScrapeUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("ProxyScrape returned upstream status code {StatusCode}.", (int)response.StatusCode);
                    throw new ProxyUpstreamException("The upstream proxy provider returned an unsuccessful response.");
                }

                if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                    throw new ProxyUpstreamException("The upstream response exceeded the configured size limit.");

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                while (true)
                {
                    var bytesRead = await stream.ReadAsync(chunk, cancellationToken);
                    if (bytesRead == 0) break;
                    if (buffer.Length + bytesRead > MaximumResponseBytes)
                        throw new ProxyUpstreamException("The upstream response exceeded the configured size limit.");
                    await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
                }

                using var document = JsonDocument.Parse(buffer.ToArray());
                var proxies = new Dictionary<string, ProxyEntry>(StringComparer.OrdinalIgnoreCase);
                ExtractProxies(document.RootElement, proxies);
                var ordered = proxies.Values
                    .OrderBy(proxy => proxy.Protocol, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(proxy => proxy.Address, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new ProxyListResponse(ordered, DateTimeOffset.UtcNow);
            });

            return Ok(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception, "ProxyScrape request timed out.");
            return Problem(statusCode: StatusCodes.Status504GatewayTimeout,
                title: "Proxy provider timed out",
                detail: "The proxy provider did not respond in time. Please retry.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "ProxyScrape request failed.");
            return Problem(statusCode: StatusCodes.Status502BadGateway,
                title: "Proxy provider unavailable",
                detail: "The proxy provider could not be reached. Please retry later.");
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "ProxyScrape returned invalid JSON.");
            return Problem(statusCode: StatusCodes.Status502BadGateway,
                title: "Invalid proxy provider response",
                detail: "The proxy provider returned data that could not be parsed.");
        }
        catch (ProxyUpstreamException exception)
        {
            logger.LogWarning(exception, "ProxyScrape returned an unusable response.");
            return Problem(statusCode: StatusCodes.Status502BadGateway,
                title: "Proxy provider unavailable",
                detail: exception.Message);
        }
    }

    private static void ExtractProxies(JsonElement element, IDictionary<string, ProxyEntry> proxies)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            AddProxy(element.GetString(), proxies);
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ExtractProxies(item, proxies);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        var protocol = ReadString(element, "protocol", "scheme", "type");
        var host = ReadString(element, "ip", "host", "hostname");
        var port = ReadPort(element, "port");

        if (!string.IsNullOrWhiteSpace(protocol) &&
            !string.IsNullOrWhiteSpace(host) &&
            port is > 0 and <= 65535)
        {
            AddProxy($"{protocol}://{host}:{port}", proxies);
        }

        foreach (var property in element.EnumerateObject())
            ExtractProxies(property.Value, proxies);
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private static int? ReadPort(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numericPort))
            return numericPort;

        return value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), out var stringPort) ? stringPort : null;
    }

    private static void AddProxy(string? candidate, IDictionary<string, ProxyEntry> proxies)
    {
        if (string.IsNullOrWhiteSpace(candidate) ||
            !Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 ||
            uri.Port is < 1 or > 65535 ||
            uri.Scheme is not ("http" or "https" or "socks4" or "socks5") ||
            !IPAddress.TryParse(uri.Host, out _))
        {
            return;
        }

        var protocol = uri.Scheme.ToLowerInvariant();
        var address = $"{protocol}://{uri.Host}:{uri.Port}";
        proxies.TryAdd(address, new ProxyEntry(address, protocol));
    }

    public sealed record ProxyEntry(string Address, string Protocol);
    public sealed record ProxyListResponse(IReadOnlyList<ProxyEntry> Proxies, DateTimeOffset FetchedAt);

    private sealed class ProxyUpstreamException(string message) : Exception(message);
}
