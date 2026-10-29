namespace ContentManagement.Server.Domain;

/// <summary>
/// Stores the single system-wide selected proxy. Id is always 1 so the database
/// primary key enforces the one-pinned-proxy invariant across app instances.
/// </summary>
public sealed class PinnedProxy
{
    public int Id { get; set; } = 1;
    public string Address { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public DateTime PinnedUtc { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    public long? LatencyMilliseconds { get; set; }
    public bool? IsHealthy { get; set; }
}