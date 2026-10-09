using System.ComponentModel.DataAnnotations;
namespace ContentManagement.Server.Configuration;
public sealed class ContentManagementOptions
{
    public const string SectionName = "ContentManagement";
    [Required]
    public string StorageRoot { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContentManagement", "files");
    [Range(1, long.MaxValue)]
    public long MaxUploadBytes { get; init; } = 104_857_600;
}
