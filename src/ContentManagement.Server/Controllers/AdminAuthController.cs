using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentManagement.Server.Auth;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AdminAuthController(
    ContentManagementDbContext db,
    IEmailJobQueue emailJobQueue,
    IOptions<AdminAuthOptions> adminOptions,
    ILogger<AdminAuthController> logger) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("request-otp")]
    [EnableRateLimiting("otp-request")]
    public async Task<IActionResult> RequestOtp([FromBody] RequestOtpRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 254 ||
            !System.Net.Mail.MailAddress.TryCreate(request.Email.Trim(), out var address) ||
            !string.Equals(address.Address, request.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Enter a valid email address." });

        var email = address.Address.ToLowerInvariant();
        var options = adminOptions.Value;
        var isAdministrator = options.AllowedEmails.Contains(email, StringComparer.OrdinalIgnoreCase);
        var managedUser = isAdministrator ? null : await db.ManagedUsers
            .SingleOrDefaultAsync(user => user.Email == email && user.Status == ManagedUserStatus.Active, cancellationToken);
        if (!isAdministrator && managedUser is null)
        {
            // Keep the response generic to avoid disclosing which addresses are registered.
            return Accepted(new { message = "If this address is eligible, a sign-in code will be sent." });
        }

        var now = DateTime.UtcNow;
        var latest = await db.AdminOtpChallenges
            .Where(x => x.Email == email && x.ConsumedUtc == null)
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is not null && now - latest.CreatedUtc < TimeSpan.FromSeconds(options.ResendCooldownSeconds))
            return Accepted(new { message = "If this address is eligible, a sign-in code will be sent." });

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var challenge = new AdminOtpChallenge
        {
            Id = Guid.NewGuid(),
            Email = email,
            CodeHash = HashOtp(email, code, options.OtpHashKey),
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(options.OtpLifetimeMinutes),
            LastSentUtc = now
        };
        db.AdminOtpChallenges.Add(challenge);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            emailJobQueue.EnqueueOtp(email, code);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Unable to send admin sign-in OTP.");
            challenge.ConsumedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Email delivery is temporarily unavailable.");
        }

        return Accepted(new { message = "If this address is eligible, a sign-in code will be sent." });
    }

    [AllowAnonymous]
    [HttpPost("verify-otp")]
    [EnableRateLimiting("otp-verify")]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Code) ||
            request.Code.Length != 6 || request.Code.Any(character => character is < '0' or > '9'))
            return BadRequest(new { message = "Enter the email and six-digit code." });

        var email = request.Email.Trim().ToLowerInvariant();
        var isAdministrator = adminOptions.Value.AllowedEmails.Contains(email, StringComparer.OrdinalIgnoreCase);
        var managedUser = isAdministrator ? null : await db.ManagedUsers
            .SingleOrDefaultAsync(user => user.Email == email && user.Status == ManagedUserStatus.Active, cancellationToken);
        if (!isAdministrator && managedUser is null)
            return Unauthorized(new { message = "The code is invalid or expired." });

        var challenge = await db.AdminOtpChallenges
            .Where(x => x.Email == email && x.ConsumedUtc == null)
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (challenge is null || challenge.ExpiresUtc <= DateTime.UtcNow ||
            challenge.FailedAttempts >= adminOptions.Value.MaxVerificationAttempts)
            return Unauthorized(new { message = "The code is invalid or expired." });

        var submittedHash = Convert.FromHexString(HashOtp(email, request.Code, adminOptions.Value.OtpHashKey));
        var expectedHash = Convert.FromHexString(challenge.CodeHash);
        if (!CryptographicOperations.FixedTimeEquals(submittedHash, expectedHash))
        {
            challenge.FailedAttempts++;
            await db.SaveChangesAsync(cancellationToken);
            return Unauthorized(new { message = "The code is invalid or expired." });
        }

        challenge.ConsumedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (managedUser is not null)
        {
            managedUser.LastLoginUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        await SignInUserAsync(email, isAdministrator, managedUser?.PermissionsJson, cancellationToken);
        return Ok(new { email });
    }

    [AllowAnonymous]
    [HttpPost("accept-invitation")]
    [EnableRateLimiting("invitation-accept")]
    public async Task<IActionResult> AcceptInvitation([FromBody] AcceptInvitationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 256)
            return Unauthorized(new { message = "The invitation is invalid or expired." });

        var tokenHash = HashToken(request.Token);
        var user = await db.ManagedUsers.SingleOrDefaultAsync(
            candidate => candidate.InvitationTokenHash == tokenHash &&
                         candidate.Status == ManagedUserStatus.Invited,
            cancellationToken);

        if (user is null || user.InvitationExpiresUtc is null || user.InvitationExpiresUtc <= DateTime.UtcNow)
            return Unauthorized(new { message = "The invitation is invalid or expired." });

        var now = DateTime.UtcNow;
        var consumed = await db.ManagedUsers
            .Where(candidate => candidate.Id == user.Id &&
                                candidate.InvitationTokenHash == tokenHash &&
                                candidate.Status == ManagedUserStatus.Invited &&
                                candidate.InvitationExpiresUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, ManagedUserStatus.Active)
                .SetProperty(candidate => candidate.InvitationTokenHash, (string?)null)
                .SetProperty(candidate => candidate.InvitationExpiresUtc, (DateTime?)null)
                .SetProperty(candidate => candidate.ActivatedUtc, now)
                .SetProperty(candidate => candidate.UpdatedUtc, now)
                .SetProperty(candidate => candidate.LastLoginUtc, now),
                cancellationToken);
        if (consumed != 1)
            return Unauthorized(new { message = "The invitation is invalid or expired." });

        await SignInUserAsync(user.Email, false, user.PermissionsJson, cancellationToken);
        logger.LogInformation("Managed user {UserId} accepted an invitation.", user.Id);
        return Ok(new { email = user.Email });
    }

    private async Task SignInUserAsync(string email, bool isAdministrator, string? permissionsJson, CancellationToken cancellationToken)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, email),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Name, email),
            new(ClaimTypes.Role, isAdministrator ? "Administrator" : "User")
        };

        if (!isAdministrator && !string.IsNullOrWhiteSpace(permissionsJson))
        {
            var permissions = JsonSerializer.Deserialize<string[]>(permissionsJson) ?? [];
            claims.AddRange(permissions.Distinct(StringComparer.Ordinal).Select(permission => new Claim("scope", permission)));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(adminOptions.Value.SessionLifetimeHours)
            });
    }

    [Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        email = User.FindFirstValue(ClaimTypes.Email),
        role = User.FindFirstValue(ClaimTypes.Role),
        permissions = User.FindAll("scope").Select(claim => claim.Value).Distinct(StringComparer.Ordinal).OrderBy(scope => scope).ToArray()
    });

    [Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string HashOtp(string email, string code, string key) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{email}:{code}")));

    public sealed record RequestOtpRequest(string Email);
    public sealed record VerifyOtpRequest(string Email, string Code);
    public sealed record AcceptInvitationRequest(string Token);
}
