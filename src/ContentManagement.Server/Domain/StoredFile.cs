using System.ComponentModel.DataAnnotations;

namespace ContentManagement.Server.Domain;

public sealed class StoredFile
{
    public Guid Id { get; set; }

    [Required, MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    [Required, MaxLength(255)]
    public string ContentType { get; set; } = "application/octet-stream";

    [Required, MaxLength(180)]
    public string StorageKey { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    [Required, MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public ContentStatus Status { get; set; } = ContentStatus.Active;

    [MaxLength(200)]
    public string? OwnerId { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}
