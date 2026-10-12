using System.ComponentModel.DataAnnotations;

namespace ContentManagement.Server.Domain;

public sealed class QuickNote
{
    public Guid Id { get; set; }

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(20000)]
    public string Content { get; set; } = string.Empty;

    public bool IsPinned { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}
