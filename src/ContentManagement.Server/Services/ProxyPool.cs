using ContentManagement.Server.Configuration;
using Microsoft.Extensions.Options;

namespace ContentManagement.Server.Services;

/// <summary>
/// Keeps a ranked in-memory pool of proxies whose relay capability was verified.
/// Free public proxies change state within minutes, so the pool scores recent probe and
/// relay samples instead of trusting the provider's advertised uptime. The pool never writes
/// to storage; the pinned row in SQL Server remains the durable source of truth.
/// </summary>
public sealed class ProxyPool(IOptions<ProxyHealthOptions> options)
{
    private const double PriorSuccessRate = 0.5;
    private const double SampleWeight = 0.3;
    private const int LatencySampleCount = 5;
    private const double UnknownLatencyMilliseconds = 3000;
    private static readonly TimeSpan RepinCooldown = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private DateTimeOffset _lastRepinRequestUtc = DateTimeOffset.MinValue;

    public void RecordProbe(string address, string protocol, ProxyHealthResult result) =>
        Record(address, protocol, result.IsHealthy, result.LatencyMilliseconds, isProbe: true);

    public void RecordRelay(string address, string protocol, bool succeeded, long? elapsedMilliseconds) =>
        Record(address, protocol, succeeded, elapsedMilliseconds, isProbe: false);

    public ProxyPoolEntry? Find(string address)
    {
        var key = KeyOf(address);
        lock (_gate)
            return _entries.TryGetValue(key, out var entry) ? ToEntry(entry) : null;
    }

    /// <summary>
    /// Returns the best routable entry: verified, not suspect, and within the relay latency budget.
    /// </summary>
    public ProxyPoolEntry? Best(string? excludeAddress = null)
    {
        var maximumLatency = options.Value.MaximumRelayLatencyMilliseconds;
        var excluded = excludeAddress is null ? null : KeyOf(excludeAddress);
        lock (_gate)
        {
            return _entries.Values
                .Where(entry => entry.Verified && entry.SuspectSinceUtc is null)
                .Where(entry => excluded is null ||
                    !entry.Address.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                .Select(ToEntry)
                .Where(entry => entry.MedianLatencyMilliseconds is null ||
                    entry.MedianLatencyMilliseconds <= maximumLatency)
                .OrderByDescending(entry => entry.Score)
                .FirstOrDefault();
        }
    }

    public IReadOnlyList<ProxyPoolEntry> Snapshot()
    {
        lock (_gate)
            return _entries.Values.Select(ToEntry).OrderByDescending(entry => entry.Score).ToArray();
    }

    public bool IsSignificantlyBetter(ProxyPoolEntry? candidate, ProxyPoolEntry? incumbent)
    {
        if (candidate is null) return false;
        if (incumbent is null) return true;
        return candidate.ConsecutiveFailures == 0 &&
               candidate.Score > incumbent.Score * options.Value.ImprovementThreshold;
    }

    public bool IsSuspect(string address)
    {
        var key = KeyOf(address);
        lock (_gate)
            return _entries.TryGetValue(key, out var entry) && entry.SuspectSinceUtc is not null;
    }

    public List<(string Address, string Protocol)> SelectProbeTargets(
        IReadOnlyList<(string Address, string Protocol)> candidates, DateTimeOffset now)
    {
        var maximumProbes = options.Value.MaxProbesPerRun;
        var poolSize = options.Value.PoolSize;
        var revalidateAfter = TimeSpan.FromMinutes(options.Value.RevalidateMinutes);
        var targets = new List<(string Address, string Protocol)>();
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            var routable = _entries.Values.Count(entry => entry.Verified && entry.SuspectSinceUtc is null);
            var added = 0;
            foreach (var entry in _entries.Values
                         .OrderByDescending(entry => entry.SuspectSinceUtc.HasValue)
                         .ThenBy(entry => entry.LastProbeUtc ?? DateTimeOffset.MinValue))
            {
                if (targets.Count >= maximumProbes) break;
                if (entry.LastProbeUtc is { } lastProbe && now - lastProbe < revalidateAfter) continue;
                if (!hosts.Add(HostOf(entry.Address))) continue;
                targets.Add((entry.Address, entry.Protocol));
            }

            foreach (var candidate in candidates)
            {
                if (targets.Count >= maximumProbes) break;
                if (routable + added >= poolSize) break;
                if (_entries.ContainsKey(candidate.Address)) continue;
                if (!hosts.Add(HostOf(candidate.Address))) continue;
                targets.Add(candidate);
                added++;
            }
        }

        return targets;
    }

    public void Prune(string? protectAddress = null)
    {
        var maximumEntries = options.Value.PoolSize * 2;
        lock (_gate)
        {
            if (_entries.Count <= maximumEntries) return;
            foreach (var stale in _entries.Values
                         .Where(entry => protectAddress is null ||
                             !entry.Address.Equals(protectAddress, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(ComputeScore)
                         .Take(_entries.Count - maximumEntries)
                         .ToArray())
                _entries.Remove(stale.Address);
        }
    }

    public bool TryRequestRepin()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (now - _lastRepinRequestUtc < RepinCooldown) return false;
            _lastRepinRequestUtc = now;
            return true;
        }
    }

    public bool TryBeginRefresh() => _refreshGate.Wait(0);

    public void EndRefresh() => _refreshGate.Release();

    private void Record(string address, string protocol, bool succeeded, long? latencyMilliseconds, bool isProbe)
    {
        if (!ProxyAddressRules.TryNormalize(address, out var normalized, out var normalizedProtocol))
            return;
        if (ProxyAddressRules.IsSupportedScheme(protocol)) normalizedProtocol = protocol;

        var now = DateTimeOffset.UtcNow;
        var failureThreshold = options.Value.PassiveFailureThreshold;
        lock (_gate)
        {
            if (!_entries.TryGetValue(normalized, out var entry))
            {
                entry = new Entry(normalized, normalizedProtocol);
                _entries[normalized] = entry;
            }

            entry.LastSampleUtc = now;
            entry.SuccessRate = entry.SuccessRate * (1 - SampleWeight) + (succeeded ? 1 : 0) * SampleWeight;
            if (succeeded)
            {
                entry.ConsecutiveFailures = 0;
                entry.SuspectSinceUtc = null;
                if (isProbe) entry.Verified = true;
                if (latencyMilliseconds is { } latency)
                {
                    entry.Latencies.Enqueue(latency);
                    while (entry.Latencies.Count > LatencySampleCount) entry.Latencies.Dequeue();
                }
            }
            else if (++entry.ConsecutiveFailures >= failureThreshold)
            {
                entry.SuspectSinceUtc ??= now;
            }

            if (isProbe) entry.LastProbeUtc = now;
        }
    }

    private static ProxyPoolEntry ToEntry(Entry entry)
    {
        long? medianLatency = entry.Latencies.Count == 0 ? null : Median(entry.Latencies);
        return new ProxyPoolEntry(
            entry.Address,
            entry.Protocol,
            Math.Round(entry.SuccessRate, 3),
            entry.ConsecutiveFailures,
            medianLatency,
            entry.Verified,
            entry.SuspectSinceUtc is not null,
            entry.LastProbeUtc,
            entry.LastSampleUtc,
            Math.Round(ComputeScore(entry), 4));
    }

    private static double ComputeScore(Entry entry)
    {
        var latency = entry.Latencies.Count == 0
            ? UnknownLatencyMilliseconds
            : Median(entry.Latencies);
        var latencyFactor = 1000.0 / (1000.0 + latency);
        var failurePenalty = Math.Pow(0.5, Math.Min(entry.ConsecutiveFailures, 4));
        var verifiedFactor = entry.Verified ? 1.0 : 0.3;
        return entry.SuccessRate * entry.SuccessRate * latencyFactor * failurePenalty * verifiedFactor;
    }

    private static long Median(IEnumerable<long> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        return ordered.Length == 0 ? 0 : ordered[ordered.Length / 2];
    }

    private static string HostOf(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : address;

    private static string KeyOf(string address) =>
        ProxyAddressRules.TryNormalize(address, out var normalized, out _) ? normalized : address;

    private sealed class Entry(string address, string protocol)
    {
        public string Address { get; } = address;
        public string Protocol { get; } = protocol;
        public double SuccessRate { get; set; } = PriorSuccessRate;
        public int ConsecutiveFailures { get; set; }
        public bool Verified { get; set; }
        public DateTimeOffset? SuspectSinceUtc { get; set; }
        public DateTimeOffset? LastProbeUtc { get; set; }
        public DateTimeOffset? LastSampleUtc { get; set; }
        public Queue<long> Latencies { get; } = new();
    }
}

public sealed record ProxyPoolEntry(
    string Address,
    string Protocol,
    double SuccessRate,
    int ConsecutiveFailures,
    long? MedianLatencyMilliseconds,
    bool Verified,
    bool IsSuspect,
    DateTimeOffset? LastProbeUtc,
    DateTimeOffset? LastSampleUtc,
    double Score);
