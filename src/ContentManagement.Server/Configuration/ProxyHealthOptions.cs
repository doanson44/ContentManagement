namespace ContentManagement.Server.Configuration;

public sealed class ProxyHealthOptions
{
    public const string SectionName = "ProxyHealth";

    /// <summary>
    /// Public endpoint used to prove that a proxy relays traffic. It must not require
    /// credentials or return sensitive data because untrusted proxies observe the request.
    /// </summary>
    public string ProbeUrl { get; init; } = "https://example.com/";

    public int ProbeTimeoutSeconds { get; init; } = 8;

    public int MaximumRelayLatencyMilliseconds { get; init; } = 8000;

    /// <summary>
    /// Number of attempts per health check. Retrying a failed probe absorbs transient
    /// network hiccups; persistent failure is tracked by the pool's failure counter.
    /// </summary>
    public int ProbeSamples { get; init; } = 2;

    public int PoolSize { get; init; } = 10;

    public int MaxProbesPerRun { get; init; } = 12;

    public int RevalidateMinutes { get; init; } = 5;

    /// <summary>
    /// Consecutive failures after which a proxy is treated as unusable for routing.
    /// </summary>
    public int PassiveFailureThreshold { get; init; } = 2;

    /// <summary>
    /// A candidate must beat the pinned proxy's score by this factor before it replaces it.
    /// The margin prevents swapping between proxies of similar quality.
    /// </summary>
    public double ImprovementThreshold { get; init; } = 1.25;

    public Uri ProbeUri => new(ProbeUrl);
}
