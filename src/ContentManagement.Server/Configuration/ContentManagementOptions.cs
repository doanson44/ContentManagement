using System.ComponentModel.DataAnnotations;

namespace ContentManagement.Server.Configuration;

public sealed class ContentManagementOptions
{
    public const string SectionName = "ContentManagement";

    // Keep persistent binaries beside (not inside) the deployment directory so replacing the ZIP
    // does not replace the data. The hosting identity must have write access to this sibling directory.
    [Required]
    public string StorageRoot { get; init; } = GetDefaultStorageRoot();

    private static string GetDefaultStorageRoot()
    {
        var deploymentDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var parentDirectory = deploymentDirectory.Parent?.FullName ?? deploymentDirectory.FullName;
        return Path.Combine(parentDirectory, "ContentManagement-Data", "files");
    }

    [Range(1, long.MaxValue)]
    public long MaxUploadBytes { get; init; } = 104_857_600;

    // Keep preview artifacts outside wwwroot. Set FfmpegPath to an absolute executable path in production.
    public string? FfmpegPath { get; init; }

    public string? PreviewCacheRoot { get; init; }

    public string? PublicBaseUrl { get; init; }

    [Range(0, long.MaxValue)]
    public long MaxPreviewCacheBytes { get; init; } = 2L * 1024 * 1024 * 1024;

}
