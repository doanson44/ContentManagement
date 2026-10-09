using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ContentManagement.Server.Auth;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.Extensions.Options;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AdminAuthController(
    ContentManagementDbContext db,
    IEmailSender emailSender,
    IOptions<AdminAuthOptions> adminOptions,
    ILogger<AdminAuthController> logger) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("request-otp")]
    [EnableRateLimiting("otp-request")]
    public async Task<IActionResult> RequestOtp(CancellationToken cancellationToken)
    {
        var allowedEmails = adminOptions.Value.AllowedEmails;
        if (allowedEmails.Length != 1 || !TryNormalizeEmail(allowedEmails[0], out var email))
        {
            logger.LogError("Administrator OTP login requires exactly one valid AdminAuth:AllowedEmails entry.");
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Administrator sign-in is not configured.");
        }

        var options = adminOptions.Value;
        var now = DateTime.UtcNow;
        var latest = await db.AdminOtpChallenges
            .Where(x => x.Email == email && x.ConsumedUtc == null)
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is not null && now - latest.CreatedUtc < TimeSpan.FromSeconds(options.ResendCooldownSeconds))
            return Accepted(new { message = "A sign-in code has already been sent recently. Check the administrator mailbox." });

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
            await emailSender.SendOtpAsync(email, code, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Unable to send administrator sign-in OTP.");
            challenge.ConsumedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Email delivery is temporarily unavailable.");
        }

        return Accepted(new { message = "The sign-in code was sent to the configured administrator mailbox." });
    }

    [AllowAnonymous]
    [HttpPost("verify-otp")]
    [EnableRateLimiting("otp-verify")]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpRequest request, CancellationToken cancellationToken)
    {
        var allowedEmails = adminOptions.Value.AllowedEmails;
        if (allowedEmails.Length != 1 || !TryNormalizeEmail(allowedEmails[0], out var email))
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Administrator sign-in is not configured.");

        if (string.IsNullOrWhiteSpace(request.Code) ||
            request.Code.Length != 6 ||
            request.Code.Any(character => character is < '0' or > '9'))
            return BadRequest(new { message = "Enter the six-digit code from the administrator email." });

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
        await SignInAdministratorAsync(email, cancellationToken);
        return Ok(new { email });
    }

    private async Task SignInAdministratorAsync(string email, CancellationToken cancellationToken)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, email),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Name, email),
            new(ClaimTypes.Role, "Administrator")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(adminOptions.Value.SessionLifetimeHours)
            });
    }

    [Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Roles = "Administrator")]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        email = User.FindFirstValue(ClaimTypes.Email),
        role = "Administrator",
        permissions = Array.Empty<string>()
    });

    [Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Roles = "Administrator")]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
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

    private static string HashOtp(string email, string code, string key) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{email}:{code}")));

    public sealed record VerifyOtpRequest(string Code);
}
