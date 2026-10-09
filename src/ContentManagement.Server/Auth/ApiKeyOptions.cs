using System.ComponentModel.DataAnnotations;

namespace ContentManagement.Server.Auth;

public sealed class ApiKeyOptions
{
    public const string SectionName = "Authentication";

    [Required]
    public string ApiKey { get; init; } = string.Empty;

    public string[] Scopes { get; init; } = [];
}
