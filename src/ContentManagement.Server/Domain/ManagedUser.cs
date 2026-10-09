using System.ComponentModel.DataAnnotations;

namespace ContentManagement.Server.Domain;

public enum ManagedUserStatus
{
    Invited = 0,
    Active = 1,
    Disabled = 2
}

public sealed class ManagedUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(254)]
    public string Email { get; set; } = "";

    public ManagedUserStatus Status { get; set; } = ManagedUserStatus.Invited;

    [MaxLength(2000)]
    public string PermissionsJson { get; set; } = "[]";

    [MaxLength(64)]
    public string? InvitationTokenHash { get; set; }

    public DateTime? InvitationExpiresUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? InvitedUtc { get; set; }

    public DateTime? ActivatedUtc { get; set; }

    public DateTime? LastLoginUtc { get; set; }
}
