namespace ContentManagement.Server.Domain;

public sealed class SystemSettings
{
    public int Id { get; set; } = 1;

    public int StaleFileAgeDays { get; set; } = 90;

    public long ExternalStorageQuotaBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    public bool ExternalStoragePreferred { get; set; }

    public bool ProxySwapEnabled { get; set; }

    public int ProxySwapIntervalMinutes { get; set; } = 60;
}
