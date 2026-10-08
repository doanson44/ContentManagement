using System.ComponentModel.DataAnnotations;
namespace ContentManagement.Configuration;
public sealed class ContentManagementOptions { public const string SectionName = "ContentManagement"; [Required] public AuthenticationOptions Authentication { get; init; } = new(); [Required] public StorageOptions Storage { get; init; } = new(); }
public sealed class AuthenticationOptions { [Required] public string Mode { get; init; } = "StaticKey"; public string? StaticKey { get; init; } }
public sealed class StorageOptions { [Required] public string RootPath { get; init; } = "Storage"; }
