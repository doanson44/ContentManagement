using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ContentManagement.Server.Auth;
using ContentManagement.Server.Configuration;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using ContentManagement.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
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
    IOptions<ProxyHealthOptions> healthOptions,
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

        // Do not let the relay become an SSRF path into loopback, private networks, or cloud metadata.
        var targetSafety = await ValidatePublicTargetAsync(targetUri!, cancellationToken);
        if (targetSafety is not null)
            return BadRequest(new ProblemDetails { Title = "Target URL is not allowed", Detail = targetSafety });

        var method = new HttpMethod(request.Method.Trim().ToUpperInvariant());
        var query = targetUri!.ToString();
        if (request.QueryParameters is { Count: > 0 })
            query = QueryHelpers.AddQueryString(query, request.QueryParameters);

        if (FindHeaderError(method, query, request.Headers) is { } headerError)
            return BadRequest(new ProblemDetails { Title = "Invalid request header", Detail = headerError });

        var useCache = request.UseCache && method == HttpMethod.Get && !HasCredentialHeaders(request.Headers);
        var cacheKey = useCache ? BuildCacheKey(query, method, request.Headers) : "";
        if (useCache && memoryCache.TryGetValue(cacheKey, out ProxyResponse? cached) && cached is not null)
            return Ok(cached with { FromCache = true });

        // A single pinned proxy is a single point of failure, so each request walks a short queue:
        // the pin first, then the best verified pool entries. The request timeout is split across
        // the attempts, which keeps the worst case bounded by what the caller asked for.
        var attempts = proxyPool.BuildAttemptQueue(pinned.Address, pinned.Protocol, healthOptions.Value.MaxRelayAttempts);
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        var totalBudget = TimeSpan.FromSeconds(request.TimeoutSeconds);
        var minimumBudget = TimeSpan.FromSeconds(Math.Clamp(healthOptions.Value.MinimumAttemptBudgetSeconds, 1, request.TimeoutSeconds));
        var stopwatch = Stopwatch.StartNew();
        var attemptsMade = 0;
        Exception? lastError = null;

        for (var index = 0; index < attempts.Count; index++)
        {
            var candidate = attempts[index];
            if (!Uri.TryCreate(candidate.Address, UriKind.Absolute, out var attemptProxyUri)) continue;

            var remaining = totalBudget - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            var fairShare = remaining.TotalMilliseconds / (attempts.Count - index);
            var budget = TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds,
                Math.Max(fairShare, minimumBudget.TotalMilliseconds)));
            attemptsMade++;

            try
            {
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(requestTimeout.Token);
                attemptTimeout.CancelAfter(budget);
                var response = await SendThroughProxyAsync(
                    attemptProxyUri, candidate.Address, request, method, query, attemptTimeout.Token);

                proxyPool.RecordRelay(candidate.Address, candidate.Protocol, succeeded: true,
                    elapsedMilliseconds: response.ElapsedMilliseconds);

                var result = response with { ElapsedMilliseconds = stopwatch.ElapsedMilliseconds };
                if (attemptsMade > 1)
                    logger.LogInformation(
                        "Relayed request for host {TargetHost} through fallback proxy {ProxyAddress} after {Attempts} attempt(s).",
                        targetUri.Host, candidate.Address, attemptsMade);
                if (useCache)
                    memoryCache.Set(cacheKey, result, TimeSpan.FromSeconds(request.CacheDurationSeconds));
                return Ok(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
                RecordRelayFailure(candidate.Address, candidate.Protocol);

                // Retrying cannot shrink an oversized body and must not replay a write that may
                // already have reached the target.
                if (exception is ProxyResponseTooLargeException || !ProxyRetryPolicy.ShouldRetry(method.Method, exception))
                {
                    logger.LogWarning(exception,
                        "Proxy {ProxyAddress} failed for host {TargetHost}; the attempt is not retryable.",
                        candidate.Address, targetUri.Host);
                    break;
                }

                logger.LogInformation(exception,
                    "Proxy {ProxyAddress} failed for host {TargetHost}; trying the next verified proxy.",
                    candidate.Address, targetUri.Host);
            }
        }

        return BuildRelayFailure(lastError, attemptsMade, targetUri, request);
    }

    private ActionResult<ProxyResponse> BuildRelayFailure(
        Exception? error, int attemptsMade, Uri target, ProxyRequest request)
    {
        switch (error)
        {
            case ProxyResponseTooLargeException exception:
                logger.LogWarning(exception, "Relayed response exceeded limits for host {TargetHost}.", target.Host);
                return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
                {
                    Title = "Invalid proxy response",
                    Detail = "The proxy response exceeded the 2 MiB response limit."
                });
            case OperationCanceledException exception:
                logger.LogInformation(exception, "Proxy request timed out for host {TargetHost} after {Attempts} attempt(s).",
                    target.Host, attemptsMade);
                return StatusCode(StatusCodes.Status504GatewayTimeout, new ProblemDetails
                {
                    Title = "Proxy request timed out",
                    Detail = $"The request exceeded the {request.TimeoutSeconds}-second timeout after {attemptsMade} proxy attempt(s)."
                });
            case HttpRequestException exception:
                logger.LogWarning(exception, "No proxy completed the request for host {TargetHost} after {Attempts} attempt(s).",
                    target.Host, attemptsMade);
                return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
                {
                    Title = "Proxy request failed",
                    Detail = "No available proxy could complete the request. The health job will re-pin a working proxy shortly."
                });
            case IOException exception:
                logger.LogWarning(exception, "Proxy connection was lost for host {TargetHost} after {Attempts} attempt(s).",
                    target.Host, attemptsMade);
                return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
                {
                    Title = "Proxy connection lost",
                    Detail = "Every attempted proxy closed the connection before the response completed."
                });
            default:
                logger.LogError("No proxy attempt could be started for host {TargetHost}; the pool has no routable entry.", target.Host);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
                {
                    Title = "No proxy available",
                    Detail = "No verified proxy is available right now. Pin a proxy or wait for the health check to rebuild the pool."
                });
        }
    }

    private static async Task<ProxyResponse> SendThroughProxyAsync(
        Uri proxyUri, string proxyAddress, ProxyRequest request, HttpMethod method, string query,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var handler = ProxyRelay.CreateHandler(proxyUri);
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var message = new HttpRequestMessage(method, query);
        if (request.Body is not null && method != HttpMethod.Get && method != HttpMethod.Head)
            message.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(request.Body));
        ApplyHeaders(message, request.Headers);
        if (message.Content is not null && !message.Content.Headers.Contains("Content-Type"))
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bodyBytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            MaximumResponseBodyBytes, cancellationToken);
        stopwatch.Stop();

        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
            responseHeaders[header.Key] = string.Join(", ", header.Value);
        foreach (var header in response.Content.Headers)
            responseHeaders[header.Key] = string.Join(", ", header.Value);

        return new ProxyResponse(
            (int)response.StatusCode,
            response.ReasonPhrase ?? "",
            response.Content.Headers.ContentType?.ToString(),
            responseHeaders,
            Encoding.UTF8.GetString(bodyBytes),
            stopwatch.ElapsedMilliseconds,
            proxyAddress,
            false);
    }

    private static string? FindHeaderError(HttpMethod method, string query, IReadOnlyDictionary<string, string> headers)
    {
        using var message = new HttpRequestMessage(method, query);
        foreach (var header in headers)
        {
            if (BlockedHeaders.Contains(header.Key))
                return $"The '{header.Key}' header is controlled by the server and cannot be overridden.";

            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                message.Content ??= new ByteArrayContent([]);
                if (!message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    return $"The '{header.Key}' header is not valid for this request.";
            }
        }

        return null;
    }

    private static void ApplyHeaders(HttpRequestMessage message, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var header in headers)
        {
            if (message.Headers.TryAddWithoutValidation(header.Key, header.Value)) continue;
            message.Content ??= new ByteArrayContent([]);
            message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static bool HasCredentialHeaders(IReadOnlyDictionary<string, string> headers) =>
        headers.Keys.Any(key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));

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
