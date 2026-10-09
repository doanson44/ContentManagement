using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentManagement.Server.Auth;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Roles = "Administrator")]
public sealed class AdminUsersController(
    ContentManagementDbContext db,
    IEmailSender emailSender,
    IOptions<AdminAuthOptions> authOptions,
    ILogger<AdminUsersController> logger) : ControllerBase
{
    private static readonly HashSet<string> SupportedPermissions = new(StringComparer.Ordinal)
    {
        "files.read", "files.write", "files.delete",
        "json.read", "json.write", "json.delete"
    };

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserResponse>>> List(CancellationToken cancellationToken)
    {
        var users = await db.ManagedUsers.AsNoTracking()
            .OrderBy(user => user.Email)
            .ToListAsync(cancellationToken);
        return Ok(users.Select(ToResponse).ToArray());
    }

    [HttpPost("invite")]
    public async Task<IActionResult> Invite([FromBody] InviteUserRequest request, CancellationToken cancellationToken)
    {
        if (!TryNormalizeEmail(request.Email, out var email))
            return BadRequest(new { message = "Enter a valid email address." });

        if (!TryNormalizePermissions(request.Permissions, out var permissions))
            return BadRequest(new { message = "One or more permissions are not supported." });

        if (authOptions.Value.AllowedEmails.Contains(email, StringComparer.OrdinalIgnoreCase))
            return Conflict(new { message = "This email is configured as an administrator and cannot be invited as a regular user." });

        var user = await db.ManagedUsers.SingleOrDefaultAsync(x => x.Email == email, cancellationToken);
        if (user is { Status: ManagedUserStatus.Active })
            return Conflict(new { message = "This user is already active." });

        var token = CreateToken();
        var now = DateTime.UtcNow;
        if (user is null)
        {
            user = new ManagedUser { Id = Guid.NewGuid(), Email = email, CreatedUtc = now };
            db.ManagedUsers.Add(user);
        }

        user.Status = ManagedUserStatus.Invited;
        user.PermissionsJson = JsonSerializer.Serialize(permissions);
        user.InvitationTokenHash = HashToken(token);
        user.InvitationExpiresUtc = now.AddHours(48);
        user.InvitedUtc = now;
        user.UpdatedUtc = now;
        user.ActivatedUtc = null;

        await db.SaveChangesAsync(cancellationToken);
        try
        {
            await SendInvitationAsync(email, token, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Unable to send user invitation email.");
            user.InvitationTokenHash = null;
            user.InvitationExpiresUtc = null;
            user.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Invitation email delivery is temporarily unavailable.");
        }

        logger.LogInformation("Administrator invited managed user {UserId}.", user.Id);
        return Accepted(ToResponse(user));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserResponse>> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken cancellationToken)
    {
        if (!TryNormalizePermissions(request.Permissions, out var permissions))
            return BadRequest(new { message = "One or more permissions are not supported." });

        var user = await db.ManagedUsers.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null)
            return NotFound();

        if (!Enum.TryParse<ManagedUserStatus>(request.Status, ignoreCase: true, out var status) || !Enum.IsDefined(status))
            return BadRequest(new { message = "Unsupported user status." });

        user.Status = status;
        user.PermissionsJson = JsonSerializer.Serialize(permissions);
        user.UpdatedUtc = DateTime.UtcNow;
        if (user.Status != ManagedUserStatus.Invited)
        {
            user.InvitationTokenHash = null;
            user.InvitationExpiresUtc = null;
        }
        if (user.Status == ManagedUserStatus.Active && user.ActivatedUtc is null)
            user.ActivatedUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Administrator updated managed user {UserId}.", user.Id);
        return Ok(ToResponse(user));
    }

    [HttpPost("{id:guid}/resend-invitation")]
    public async Task<IActionResult> ResendInvitation(Guid id, CancellationToken cancellationToken)
    {
        var user = await db.ManagedUsers.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null)
            return NotFound();
        if (user.Status != ManagedUserStatus.Invited)
            return Conflict(new { message = "Only invited users can receive a new invitation." });

        var token = CreateToken();
        user.InvitationTokenHash = HashToken(token);
        user.InvitationExpiresUtc = DateTime.UtcNow.AddHours(48);
        user.InvitedUtc = DateTime.UtcNow;
        user.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            await SendInvitationAsync(user.Email, token, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Unable to resend user invitation email.");
            user.InvitationTokenHash = null;
            user.InvitationExpiresUtc = null;
            user.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Invitation email delivery is temporarily unavailable.");
        }

        return Accepted();
    }

    private async Task SendInvitationAsync(string email, string token, CancellationToken cancellationToken)
    {
        var baseUrl = authOptions.Value.PublicBaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("AdminAuth:PublicBaseUrl must be configured as an absolute HTTPS URL before sending invitations.");

        var link = $"{baseUrl}/invite/confirm?token={Uri.EscapeDataString(token)}";
        await emailSender.SendInvitationAsync(email, link, cancellationToken);
    }

    private static bool TryNormalizeEmail(string? value, out string email)
    {
        email = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254 ||
            !System.Net.Mail.MailAddress.TryCreate(value.Trim(), out var address) ||
            !string.Equals(address.Address, value.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        email = address.Address.ToLowerInvariant();
        return true;
    }

    private static bool TryNormalizePermissions(IEnumerable<string>? requested, out string[] permissions)
    {
        permissions = (requested ?? []).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return permissions.All(SupportedPermissions.Contains) && permissions.Length <= SupportedPermissions.Count;
    }

    private static string CreateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static UserResponse ToResponse(ManagedUser user) =>
        new(user.Id, user.Email, user.Status.ToString(), JsonSerializer.Deserialize<string[]>(user.PermissionsJson) ?? [],
            user.CreatedUtc, user.InvitedUtc, user.InvitationExpiresUtc, user.ActivatedUtc, user.LastLoginUtc);

    public sealed record InviteUserRequest(string Email, string[] Permissions);
    public sealed record UpdateUserRequest(string Status, string[] Permissions);
    public sealed record UserResponse(Guid Id, string Email, string Status, string[] Permissions,
        DateTime CreatedUtc, DateTime? InvitedUtc, DateTime? InvitationExpiresUtc, DateTime? ActivatedUtc, DateTime? LastLoginUtc);
}
