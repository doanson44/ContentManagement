namespace ContentManagement.Server.Domain;

public sealed class AdminOtpChallenge
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? ConsumedUtc { get; set; }
    public DateTime? LastSentUtc { get; set; }
    public int FailedAttempts { get; set; }
    public string? RequestTokenHash { get; set; }
}
