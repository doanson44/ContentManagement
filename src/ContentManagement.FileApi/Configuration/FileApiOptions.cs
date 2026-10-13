using System.ComponentModel.DataAnnotations;

namespace ContentManagement.FileApi.Configuration;

public sealed class FileApiOptions
{
    public const string SectionName = "FileApi";

    [Required]
    public string RootPath { get; set; } = string.Empty;

    [Range(1, 104857600)]
    public long MaxUploadBytes { get; set; } = 104_857_600;
}
