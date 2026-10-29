using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace ContentManagement.Server.Services;

public sealed class ProxyDirectoryClient(
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    ILogger<ProxyDirectoryClient> logger)
{
    public const string HttpClientName = "ProxyScrape";
    private const string CacheKey = "proxyscrape:vietnam:protocolipport";
    private const int MaximumResponseBytes = 2 * 1024 * 1024;

    // The provider-side timeout filter keeps entries that need more than five seconds out of the
    // directory; slower endpoints cannot satisfy the relay and health-check budgets below.
    private static readonly Uri UpstreamUri = new(
        "https://api.proxyscrape.com/v4/free-proxy-list/get?request=display_proxies&proxy_format=protocolipport&format=json&country=vn&timeout=5000");

    public async Task<ProxyListResponse> GetAsync(CancellationToken cancellationToken)
    {
        if (memoryCache.TryGetValue(CacheKey, out ProxyListResponse? cached) && cached is not null)
            return cached;

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(UpstreamUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("ProxyScrape returned upstream status code {StatusCode}.", (int)response.StatusCode);
            throw new ProxyDirectoryException("The upstream proxy provider returned an unsuccessful response.");
        }

        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new ProxyDirectoryException("The upstream response exceeded the configured size limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var bytesRead = await stream.ReadAsync(chunk, cancellationToken);
            if (bytesRead == 0) break;
            if (buffer.Length + bytesRead > MaximumResponseBytes)
                throw new ProxyDirectoryException("The upstream response exceeded the configured size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        var proxies = new Dictionary<string, ProxyEntry>(StringComparer.OrdinalIgnoreCase);
        ExtractProxies(document.RootElement, proxies);
        var ordered = proxies.Values
            .OrderBy(proxy => proxy.Protocol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(proxy => proxy.Address, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = new ProxyListResponse(ordered, DateTimeOffset.UtcNow);
        memoryCache.Set(CacheKey, result, TimeSpan.FromSeconds(30));
        return result;
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

        if (!string.IsNullOrWhiteSpace(protocol) && !string.IsNullOrWhiteSpace(host) && port is > 0 and <= 65535 &&
            ProxyAddressRules.TryNormalize($"{protocol}://{host}:{port}", out var address, out var normalizedProtocol))
        {
            proxies.TryAdd(address, new ProxyEntry(
                address,
                normalizedProtocol,
                ReadString(element, "country", "country_name"),
                ReadString(element, "country_code", "countryCode", "cc"),
                ReadString(element, "city"),
                ReadString(element, "anonymity", "anonymous"),
                ReadBoolean(element, "ssl", "https", "supports_ssl"),
                ReadNumber(element, "latency_ms", "latency", "timeout_ms", "timeout"),
                ReadNumber(element, "uptime_percent", "uptime"),
                ReadString(element, "asn", "as_number"),
                ReadString(element, "isp", "organization", "org"),
                ReadTimestamp(element, "last_checked", "lastChecked", "last_seen")));
        }

        foreach (var property in element.EnumerateObject())
            ExtractProxies(property.Value, proxies);
    }

    private static void AddProxy(string? candidate, IDictionary<string, ProxyEntry> proxies)
    {
        if (ProxyAddressRules.TryNormalize(candidate, out var address, out var protocol))
            proxies.TryAdd(address, new ProxyEntry(address, protocol));
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

    private static bool? ReadBoolean(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
                return parsed;
        }

        return null;
    }

    private static double? ReadNumber(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numeric))
                return numeric;
            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out numeric))
                return numeric;
        }

        return null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed.ToUniversalTime();
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var epoch))
            {
                try
                {
                    return epoch > 100_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds((long)epoch)
                        : DateTimeOffset.FromUnixTimeSeconds((long)epoch);
                }
                catch (ArgumentOutOfRangeException) { return null; }
            }
        }

        return null;
    }
}

public sealed record ProxyEntry(
    string Address,
    string Protocol,
    string? Country = null,
    string? CountryCode = null,
    string? City = null,
    string? Anonymity = null,
    bool? SupportsSsl = null,
    double? LatencyMilliseconds = null,
    double? UptimePercent = null,
    string? Asn = null,
    string? Isp = null,
    DateTimeOffset? LastChecked = null);

public sealed record ProxyListResponse(IReadOnlyList<ProxyEntry> Proxies, DateTimeOffset FetchedAt);

public sealed class ProxyDirectoryException(string message) : Exception(message);
