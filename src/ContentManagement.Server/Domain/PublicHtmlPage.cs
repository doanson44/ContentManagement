using System.ComponentModel.DataAnnotations;

namespace ContentManagement.Server.Domain;

public sealed class PublicHtmlPage
{
    public Guid Id { get; set; }
    [Required, MaxLength(256)] public string Title { get; set; } = string.Empty;
    [Required, MaxLength(80)] public string Slug { get; set; } = string.Empty;
    [Required, MaxLength(180)] public string StorageKey { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    [Required, MaxLength(64)] public string Sha256 { get; set; } = string.Empty;
    public bool IsPublished { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime? LastViewedAtUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    [MaxLength(200)] public string? OwnerId { get; set; }
    public bool IsDeleted { get; set; }
    public bool StorageDeletePending { get; set; }
}
