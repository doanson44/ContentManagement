using System.Net.Http;

namespace ContentManagement.Server.Services;

/// <summary>
/// Decides whether a failed relay attempt may be retried through another proxy.
/// Non-idempotent methods are only retried when the failure is known to have happened before
/// the request could reach the target, because a retry could otherwise duplicate a write.
/// </summary>
public static class ProxyRetryPolicy
{
    private static readonly HashSet<string> IdempotentMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    public static bool IsIdempotent(string method) => IdempotentMethods.Contains(method);

    public static bool ShouldRetry(string method, Exception exception) => exception switch
    {
        HttpRequestException
        {
            HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.ProxyTunnelError
                or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError
        } => true,
        OperationCanceledException => IsIdempotent(method),
        HttpRequestException => IsIdempotent(method),
        IOException => IsIdempotent(method),
        _ => false
    };
}
