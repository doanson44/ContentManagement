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

    public Uri ProbeUri => new(ProbeUrl);
}
