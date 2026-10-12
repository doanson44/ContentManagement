using System.ComponentModel.DataAnnotations;

namespace ContentManagement.Server.Domain;

public sealed class ApiClient
{
    public Guid Id { get; set; }

    [Required, MaxLength(32)]
    public string ClientId { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string SecretHash { get; set; } = string.Empty;

    [Required, MaxLength(1000)]
    public string ScopesJson { get; set; } = "[]";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}
