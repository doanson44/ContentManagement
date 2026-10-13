using System.ComponentModel.DataAnnotations;

namespace ContentManagement.Server.Configuration;

/// <summary>
/// Server-only connection settings for the external binary storage API.
/// Keep credentials out of client configuration and source control.
/// </summary>
public sealed class ExternalStorageOptions
{
    public const string SectionName = "ExternalStorage";

    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(ApiKey);
}
