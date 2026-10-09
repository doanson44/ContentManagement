namespace ContentManagement.Server.Auth;

public sealed class AdminAuthOptions
{
    public const string SectionName = "AdminAuth";
    public string[] AllowedEmails { get; set; } = [];
    public string OtpHashKey { get; set; } = "";
    public int OtpLifetimeMinutes { get; set; } = 10;
    public int MaxVerificationAttempts { get; set; } = 5;
    public int ResendCooldownSeconds { get; set; } = 60;
    public int SessionLifetimeHours { get; set; } = 8;
    public string[] AllowedOrigins { get; set; } = [];
}
