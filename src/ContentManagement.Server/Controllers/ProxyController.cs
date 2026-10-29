using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ContentManagement.Server.Auth;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using ContentManagement.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.WebUtilities;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/proxy")]
public sealed class ProxyController(
    ContentManagementDbContext db,
    IMemoryCache memoryCache,
    PinnedProxyCache pinnedProxyCache,
    ProxyPool proxyPool,
    ProxyRepinTrigger repinTrigger,
    ILogger<ProxyController> logger) : ControllerBase
{
    private const int MaximumRequestBodyBytes = 1 * 1024 * 1024;
    private const int MaximumResponseBodyBytes = 2 * 1024 * 1024;
    private const int MaximumTimeoutSeconds = 120;
    private const int MaximumCacheDurationSeconds = 3600;
    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Content-Length", "Transfer-Encoding", "Proxy-Authorization",
        "Proxy-Connection", "Keep-Alive", "TE", "Trailer", "Upgrade", "Expect"
    };

    [HttpPost]
    [Authorize(Policy = ScopePolicies.ProxyRequest)]
    [ProducesResponseType<ProxyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<ProxyResponse>> PostAsync(
        [FromBody] ProxyRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequest(request, out var targetUri, out var validationError))
            return BadRequest(new ProblemDetails { Title = "Invalid proxy request", Detail = validationError });

        // Fast path: the pin operation and health job keep this snapshot cache current.
        // Query SQL Server only when the cache has not been populated yet.
        var pinned = pinnedProxyCache.Get();
        if (pinned is null)
        {
            var pinnedEntity = await db.PinnedProxies.AsNoTracking()
                .SingleOrDefaultAsync(proxy => proxy.Id == 1, cancellationToken);
            if (pinnedEntity is null)
            {
                pinnedProxyCache.Clear();
                return Conflict(new ProblemDetails
                {
                    Title = "No proxy is pinned",
                    Detail = "Pin a proxy on the Proxy Management page before sending a test request."
                });
            }

            pinned = new PinnedProxySnapshot(
                pinnedEntity.Address,
                pinnedEntity.Protocol,
                pinnedEntity.PinnedUtc,
                pinnedEntity.LastCheckedUtc,
                pinnedEntity.LatencyMilliseconds,
                pinnedEntity.IsHealthy);
            pinnedProxyCache.Set(pinned);
        }

        if (!ProxyAddressRules.TryNormalize(pinned.Address, out var normalizedProxy, out _) ||
            !IPAddress.TryParse(new Uri(normalizedProxy).Host, out var proxyIp) ||
            !ProxyAddressRules.IsPublicAddress(proxyIp))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Pinned proxy is invalid",
                Detail = "The pinned proxy address is not a supported public IP endpoint."
            });
        }

        var proxyUri = new Uri(normalizedProxy);

        // Do not let the relay become an SSRF path into loopback, private networks, or cloud metadata.
        var targetSafety = await ValidatePublicTargetAsync(targetUri!, cancellationToken);
        if (targetSafety is not null)
            return BadRequest(new ProblemDetails { Title = "Target URL is not allowed", Detail = targetSafety });

        var method = new HttpMethod(request.Method.Trim().ToUpperInvariant());
        var query = targetUri!.ToString();
        if (request.QueryParameters is { Count: > 0 })
            query = QueryHelpers.AddQueryString(query, request.QueryParameters);

        if (request.UseCache && method == HttpMethod.Get &&
            !request.Headers.Keys.Any(key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                                             key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
        {
            var cacheKey = BuildCacheKey(query, method, request.Headers);
            if (memoryCache.TryGetValue(cacheKey, out ProxyResponse? cached) && cached is not null)
                return Ok(cached with { FromCache = true });
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var handler = ProxyRelay.CreateHandler(proxyUri);
            using var client = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            using var message = new HttpRequestMessage(method, query);
            if (request.Body is not null && method != HttpMethod.Get && method != HttpMethod.Head)
                message.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(request.Body));

            foreach (var header in request.Headers)
            {
                if (BlockedHeaders.Contains(header.Key))
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Invalid request header",
                        Detail = $"The '{header.Key}' header is controlled by the server and cannot be overridden."
                    });

                if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    message.Content ??= new ByteArrayContent([]);
                    if (!message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value))
                        return BadRequest(new ProblemDetails
                        {
                            Title = "Invalid request header",
                            Detail = $"The '{header.Key}' header is not valid for this request."
                        });
                }
            }
            if (message.Content is not null && !message.Content.Headers.Contains("Content-Type"))
                message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            // Every relay is a free health sample: a completed exchange proves the proxy works.
            proxyPool.RecordRelay(pinned.Address, pinned.Protocol, succeeded: true, stopwatch.ElapsedMilliseconds);

            var bodyBytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(timeout.Token),
                MaximumResponseBodyBytes, timeout.Token);
            stopwatch.Stop();

            var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers)
                responseHeaders[header.Key] = string.Join(", ", header.Value);
            foreach (var header in response.Content.Headers)
                responseHeaders[header.Key] = string.Join(", ", header.Value);

            var result = new ProxyResponse(
                (int)response.StatusCode,
                response.ReasonPhrase ?? "",
                response.Content.Headers.ContentType?.ToString(),
                responseHeaders,
                Encoding.UTF8.GetString(bodyBytes),
                stopwatch.ElapsedMilliseconds,
                pinned.Address,
                false);

            if (request.UseCache && method == HttpMethod.Get &&
                !request.Headers.Keys.Any(key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                                                 key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
            {
                memoryCache.Set(BuildCacheKey(query, method, request.Headers), result,
                    TimeSpan.FromSeconds(request.CacheDurationSeconds));
            }

            return Ok(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogInformation(exception, "Pinned-proxy request timed out for host {TargetHost}.", targetUri!.Host);
            RecordRelayFailure(pinned.Address, pinned.Protocol);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new ProblemDetails
            {
                Title = "Proxy request timed out",
                Detail = $"The request exceeded the {request.TimeoutSeconds}-second timeout."
            });
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Pinned-proxy request failed for host {TargetHost}.", targetUri!.Host);
            RecordRelayFailure(pinned.Address, pinned.Protocol);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Proxy request failed",
                Detail = "The pinned proxy could not complete the request. Check its health or pin another proxy."
            });
        }
        catch (ProxyResponseTooLargeException exception)
        {
            logger.LogWarning(exception, "Pinned-proxy response exceeded limits for host {TargetHost}.", targetUri!.Host);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Invalid proxy response",
                Detail = "The proxy response exceeded the 2 MiB response limit."
            });
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Pinned-proxy connection was lost for host {TargetHost}.", targetUri!.Host);
            RecordRelayFailure(pinned.Address, pinned.Protocol);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Proxy connection lost",
                Detail = "The pinned proxy closed the connection before the response completed."
            });
        }
    }

    private void RecordRelayFailure(string address, string protocol)
    {
        proxyPool.RecordRelay(address, protocol, succeeded: false, elapsedMilliseconds: null);
        if (proxyPool.IsSuspect(address)) repinTrigger.TryRequest();
    }

    private static bool TryValidateRequest(ProxyRequest request, out Uri? targetUri, out string error)
    {
        targetUri = null;
        error = "";
        if (request is null || string.IsNullOrWhiteSpace(request.TargetUrl) ||
            !Uri.TryCreate(request.TargetUrl.Trim(), UriKind.Absolute, out targetUri) ||
            targetUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(targetUri.UserInfo) ||
            targetUri.Fragment.Length > 0)
        {
            error = "TargetUrl must be an absolute HTTP or HTTPS URL without embedded credentials or a fragment.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Method) ||
            !new[] { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" }
                .Contains(request.Method.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            error = "Method must be GET, HEAD, POST, PUT, PATCH, DELETE, or OPTIONS.";
            return false;
        }

        if (request.TimeoutSeconds is < 1 or > MaximumTimeoutSeconds)
        {
            error = $"TimeoutSeconds must be between 1 and {MaximumTimeoutSeconds}.";
            return false;
        }

        if (request.CacheDurationSeconds is < 1 or > MaximumCacheDurationSeconds)
        {
            error = $"CacheDurationSeconds must be between 1 and {MaximumCacheDurationSeconds}.";
            return false;
        }

        if (request.Body is { Length: > MaximumRequestBodyBytes })
        {
            error = "Body cannot exceed 1 MiB.";
            return false;
        }

        if (request.Headers is null || request.QueryParameters is null)
        {
            error = "Headers and QueryParameters must be JSON objects (use empty objects when not needed).";
            return false;
        }

        if (request.Headers.Count > 50 || request.QueryParameters.Count > 100)
        {
            error = "A maximum of 50 headers and 100 query parameters is allowed.";
            return false;
        }

        if (request.Headers.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Any(char.IsControl) ||
                                        pair.Value is null || pair.Value.Any(c => c is '\r' or '\n')))
        {
            error = "Header names and values must be valid and cannot contain line breaks.";
            return false;
        }

        if (request.QueryParameters.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
                                                 pair.Key.Any(char.IsControl) || pair.Value is null))
        {
            error = "Query parameter names must be non-empty and values must be strings.";
            return false;
        }

        return true;
    }

    private static async Task<string?> ValidatePublicTargetAsync(Uri target, CancellationToken cancellationToken)
    {
        if (target.IsLoopback || target.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            target.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            target.Host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
            return "Loopback and cloud metadata targets are blocked.";

        try
        {
            var addresses = IPAddress.TryParse(target.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(target.DnsSafeHost, cancellationToken);
            if (addresses.Length == 0 || addresses.Any(address => !ProxyAddressRules.IsPublicAddress(address)))
                return "The target must resolve only to public IP addresses.";
        }
        catch (SocketException)
        {
            return "The target host could not be resolved.";
        }

        return null;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        await using (stream)
        using (var output = new MemoryStream())
        {
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) return output.ToArray();
                if (output.Length + read > limit)
                    throw new ProxyResponseTooLargeException();
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
    }

    private static string BuildCacheKey(string url, HttpMethod method, IReadOnlyDictionary<string, string> headers)
    {
        var canonical = method.Method + "\n" + url + "\n" +
            string.Join("\n", headers.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key.ToLowerInvariant() + ":" + pair.Value));
        return "proxy-response:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public sealed record ProxyRequest
    {
        public string TargetUrl { get; init; } = "";
        public string Method { get; init; } = "GET";
        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> QueryParameters { get; init; } = new();
        public string? Body { get; init; }
        public int TimeoutSeconds { get; init; } = 30;
        public bool UseCache { get; init; }
        public int CacheDurationSeconds { get; init; } = 60;
    }

    public sealed record ProxyResponse(
        int StatusCode,
        string ReasonPhrase,
        string? ContentType,
        IReadOnlyDictionary<string, string> Headers,
        string Body,
        long ElapsedMilliseconds,
        string UsedProxy,
        bool FromCache);

    private sealed class ProxyResponseTooLargeException()
        : IOException("Response size limit exceeded.");
}
