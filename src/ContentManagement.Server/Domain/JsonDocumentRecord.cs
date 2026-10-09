using System.ComponentModel.DataAnnotations;

namespace ContentManagement.Server.Domain;

public sealed class JsonDocumentRecord
{
    public Guid Id { get; set; }

    [Required, MaxLength(200)]
    public string DocumentType { get; set; } = string.Empty;

    public byte[] CompressedPayload { get; set; } = [];

    public long UncompressedSizeBytes { get; set; }

    public long CompressedSizeBytes { get; set; }

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
