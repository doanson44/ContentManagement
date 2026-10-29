using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.WebUtilities;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/proxy")]
[Authorize(Roles = "Administrator")]
public sealed class ProxyController(
    ContentManagementDbContext db,
    IMemoryCache memoryCache,
    ILogger<ProxyController> logger) : ControllerBase
{
    private const int MaximumRequestBodyBytes = 1 * 1024 * 1024;
    private const int MaximumResponseBodyBytes = 2 * 1024 * 1024;
    private const int MaximumTimeoutSeconds = 60;
    private const int MaximumCacheDurationSeconds = 3600;
    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Content-Length", "Transfer-Encoding", "Proxy-Authorization",
        "Proxy-Connection", "Keep-Alive", "TE", "Trailer", "Upgrade", "Expect"
    };

    [HttpPost]
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

        var pinned = await db.PinnedProxies.AsNoTracking()
            .SingleOrDefaultAsync(proxy => proxy.Id == 1, cancellationToken);
        if (pinned is null)
            return Conflict(new ProblemDetails
            {
                Title = "No proxy is pinned",
                Detail = "Pin a proxy on the Proxy Management page before sending a test request."
            });

        if (!Uri.TryCreate(pinned.Address, UriKind.Absolute, out var proxyUri) ||
            proxyUri.Scheme is not ("http" or "https" or "socks4" or "socks5") ||
            !IPAddress.TryParse(proxyUri.Host, out var proxyIp) || !IsPublicAddress(proxyIp))
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
            using var handler = CreateHandler(proxyUri!);
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
            return StatusCode(StatusCodes.Status504GatewayTimeout, new ProblemDetails
            {
                Title = "Proxy request timed out",
                Detail = $"The request exceeded the {request.TimeoutSeconds}-second timeout."
            });
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Pinned-proxy request failed for host {TargetHost}.", targetUri!.Host);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Proxy request failed",
                Detail = "The pinned proxy could not complete the request. Check its health or pin another proxy."
            });
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Pinned-proxy response exceeded limits or could not be read for host {TargetHost}.", targetUri!.Host);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Invalid proxy response",
                Detail = "The proxy response could not be read or exceeded the 2 MiB response limit."
            });
        }
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
            if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
                return "The target must resolve only to public IP addresses.";
        }
        catch (SocketException)
        {
            return "The target host could not be resolved.";
        }

        return null;
    }

    private static HttpMessageHandler CreateHandler(Uri proxyUri)
    {
        if (proxyUri.Scheme is "socks4" or "socks5")
        {
            return new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectCallback = (context, token) => ConnectThroughSocksAsync(proxyUri, context.DnsEndPoint, token)
            };
        }

        return new HttpClientHandler
        {
            Proxy = new WebProxy(new UriBuilder(Uri.UriSchemeHttp, proxyUri.Host, proxyUri.Port).Uri),
            UseProxy = true,
            UseCookies = false,
            AllowAutoRedirect = false
        };
    }

    private static async ValueTask<Stream> ConnectThroughSocksAsync(
        Uri proxyUri, DnsEndPoint target, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(proxyUri.Host, proxyUri.Port, cancellationToken);
            var stream = client.GetStream();
            if (proxyUri.Scheme == "socks5")
            {
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken);
                var greeting = new byte[2];
                await ReadExactlyAsync(stream, greeting, cancellationToken);
                if (greeting[0] != 5 || greeting[1] != 0)
                    throw new HttpRequestException("The SOCKS5 proxy does not support unauthenticated connections.");

                var hostBytes = Encoding.ASCII.GetBytes(target.Host);
                if (hostBytes.Length > 255) throw new HttpRequestException("Target host name is too long for SOCKS5.");
                var request = new byte[7 + hostBytes.Length];
                request[0] = 5; request[1] = 1; request[2] = 0; request[3] = 3;
                request[4] = (byte)hostBytes.Length;
                hostBytes.CopyTo(request, 5);
                request[^2] = (byte)(target.Port >> 8);
                request[^1] = (byte)target.Port;
                await stream.WriteAsync(request, cancellationToken);

                var reply = new byte[4];
                await ReadExactlyAsync(stream, reply, cancellationToken);
                if (reply[0] != 5 || reply[1] != 0)
                    throw new HttpRequestException($"SOCKS5 connection failed with status {reply[1]}.");
                var remaining = reply[3] switch
                {
                    1 => 4,
                    4 => 16,
                    3 => await ReadOneLengthAsync(stream, cancellationToken),
                    _ => throw new HttpRequestException("SOCKS5 returned an invalid address type.")
                };
                var discard = new byte[remaining + 2];
                await ReadExactlyAsync(stream, discard, cancellationToken);
            }
            else
            {
                // SOCKS4a delegates DNS resolution to the proxy while retaining the same outbound guard.
                var hostBytes = Encoding.ASCII.GetBytes(target.Host);
                var request = new byte[9 + hostBytes.Length];
                request[0] = 4; request[1] = 1;
                request[2] = (byte)(target.Port >> 8); request[3] = (byte)target.Port;
                request[4] = 0; request[5] = 0; request[6] = 0; request[7] = 1;
                request[8] = 0;
                hostBytes.CopyTo(request, 9);
                await stream.WriteAsync(request, cancellationToken);
                await stream.WriteAsync(new byte[] { 0 }, cancellationToken);
                var reply = new byte[8];
                await ReadExactlyAsync(stream, reply, cancellationToken);
                if (reply[1] != 90)
                    throw new HttpRequestException($"SOCKS4 connection failed with status {reply[1]}.");
            }

            return new OwnedNetworkStream(stream, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<int> ReadOneLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[1];
        await ReadExactlyAsync(stream, length, cancellationToken);
        return length[0];
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new IOException("Proxy closed the connection during protocol negotiation.");
            offset += read;
        }
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
                    throw new IOException("Response size limit exceeded.");
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

    private static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal)
            return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] != 0 && bytes[0] != 10 && bytes[0] != 127 &&
                !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && bytes[1] == 168) &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) &&
                !(bytes[0] == 198 && bytes[1] is 18 or 19) && bytes[0] < 224;
        }
        var bytes6 = ip.GetAddressBytes();
        return !ip.Equals(IPAddress.IPv6Any) && !(bytes6[0] == 0xFC || bytes6[0] == 0xFD) &&
            !(bytes6[0] == 0xFE && (bytes6[1] & 0xC0) == 0x80);
    }

    private sealed class OwnedNetworkStream(Stream inner, TcpClient owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); owner.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); owner.Dispose(); GC.SuppressFinalize(this); }
    }

    public sealed record ProxyRequest
    {
        public string TargetUrl { get; init; } = "";
        public string Method { get; init; } = "GET";
        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> QueryParameters { get; init; } = new();
        public string? Body { get; init; }
        public int TimeoutSeconds { get; init; } = 15;
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
}
