using System.ComponentModel.DataAnnotations;

namespace ContentManagement.Server.Configuration;

public sealed class ContentManagementOptions
{
    public const string SectionName = "ContentManagement";

    [Required]
    public string StorageRoot { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContentManagement", "files");

    [Range(1, long.MaxValue)]
    public long MaxUploadBytes { get; init; } = 104_857_600;

    // Keep preview artifacts outside wwwroot. Set FfmpegPath to an absolute executable path in production.
    public string? FfmpegPath { get; init; }

    public string? PreviewCacheRoot { get; init; }

    [Range(0, long.MaxValue)]
    public long MaxPreviewCacheBytes { get; init; } = 2L * 1024 * 1024 * 1024;

}
