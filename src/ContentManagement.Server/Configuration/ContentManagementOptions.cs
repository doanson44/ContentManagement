using System.ComponentModel.DataAnnotations;

namespace ContentManagement.Server.Configuration;

public sealed class ContentManagementOptions
{
    public const string SectionName = "ContentManagement";

    // Avoid LocalApplicationData: Windows services and IIS may resolve it to a protected systemprofile directory.
    // Path.GetTempPath() normally resolves to the service's writable Windows temp directory.
    // Configure StorageRoot to a dedicated persistent directory for production data when possible.
    [Required]
    public string StorageRoot { get; init; } = Path.Combine(Path.GetTempPath(), "ContentManagement", "files");

    [Range(1, long.MaxValue)]
    public long MaxUploadBytes { get; init; } = 104_857_600;

    // Keep preview artifacts outside wwwroot. Set FfmpegPath to an absolute executable path in production.
    public string? FfmpegPath { get; init; }

    public string? PreviewCacheRoot { get; init; }

    public string? PublicBaseUrl { get; init; }

    [Range(0, long.MaxValue)]
    public long MaxPreviewCacheBytes { get; init; } = 2L * 1024 * 1024 * 1024;

}
