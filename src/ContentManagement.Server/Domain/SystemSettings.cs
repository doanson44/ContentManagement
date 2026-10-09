using System.ComponentModel.DataAnnotations;

namespace ContentManagement.Server.Domain;

public sealed class SystemSettings
{
    public int Id { get; set; } = 1;

    [MaxLength(255)]
    public string SmtpHost { get; set; } = "";

    public int SmtpPort { get; set; } = 587;

    public bool SmtpUseSsl { get; set; } = true;

    [MaxLength(255)]
    public string SmtpUsername { get; set; } = "";

    [MaxLength(2048)]
    public string SmtpPassword { get; set; } = "";

    [MaxLength(254)]
    public string SmtpFromEmail { get; set; } = "";

    [MaxLength(200)]
    public string SmtpFromName { get; set; } = "ContentManagement";

    [MaxLength(254)]
    public string TestRecipientEmail { get; set; } = "";

    public int StaleFileAgeDays { get; set; } = 90;

    public int CleanupIntervalHours { get; set; } = 24;

    public int DeletionGracePeriodDays { get; set; } = 7;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
