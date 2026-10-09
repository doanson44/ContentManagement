using System.ComponentModel.DataAnnotations;
using ContentManagement.Server.Auth;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/admin/setup")]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Roles = "Administrator")]
public sealed class AdminSetupController(
    ContentManagementDbContext db,
    IEmailSender emailSender,
    ILogger<AdminSetupController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SetupResponse>> Get(CancellationToken cancellationToken)
    {
        var settings = await GetOrCreateSettingsAsync(cancellationToken);
        return Ok(ToResponse(settings));
    }

    [HttpPut]
    public async Task<ActionResult<SetupResponse>> Save([FromBody] UpdateSetupRequest request, CancellationToken cancellationToken)
    {
        if (request.SmtpPort is < 1 or > 65535 ||
            request.StaleFileAgeDays is < 1 or > 3650 ||
            request.CleanupIntervalHours is < 1 or > 24 ||
            request.DeletionGracePeriodDays is < 1 or > 90)
            return BadRequest(new { message = "SMTP port or cleanup values are outside the allowed range." });

        if (!string.IsNullOrWhiteSpace(request.SmtpFromEmail) &&
            (!System.Net.Mail.MailAddress.TryCreate(request.SmtpFromEmail, out var sender) ||
             !string.Equals(sender.Address, request.SmtpFromEmail.Trim(), StringComparison.OrdinalIgnoreCase)))
            return BadRequest(new { message = "Enter a valid sender email address." });

        if (string.IsNullOrWhiteSpace(request.SmtpHost) != string.IsNullOrWhiteSpace(request.SmtpFromEmail))
            return BadRequest(new { message = "SMTP host and sender email must both be provided." });

        var settings = await GetOrCreateSettingsAsync(cancellationToken);
        settings.SmtpHost = request.SmtpHost.Trim();
        settings.SmtpPort = request.SmtpPort;
        settings.SmtpUseSsl = request.SmtpUseSsl;
        settings.SmtpUsername = request.SmtpUsername.Trim();
        if (!string.IsNullOrEmpty(request.SmtpPassword))
            settings.SmtpPassword = request.SmtpPassword;
        if (string.IsNullOrWhiteSpace(settings.SmtpUsername))
            settings.SmtpPassword = "";
        settings.SmtpFromEmail = request.SmtpFromEmail.Trim();
        settings.SmtpFromName = string.IsNullOrWhiteSpace(request.SmtpFromName)
            ? "ContentManagement"
            : request.SmtpFromName.Trim();
        settings.TestRecipientEmail = request.TestRecipientEmail.Trim();
        settings.StaleFileAgeDays = request.StaleFileAgeDays;
        settings.CleanupIntervalHours = request.CleanupIntervalHours;
        settings.DeletionGracePeriodDays = request.DeletionGracePeriodDays;
        settings.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Administrator updated system setup settings.");
        return Ok(ToResponse(settings));
    }

    [HttpPost("test-email")]
    public async Task<IActionResult> TestEmail([FromBody] TestEmailRequest request, CancellationToken cancellationToken)
    {
        if (!System.Net.Mail.MailAddress.TryCreate(request.RecipientEmail, out var recipient) ||
            !string.Equals(recipient.Address, request.RecipientEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Enter a valid recipient email address." });

        try
        {
            await emailSender.SendTestAsync(recipient.Address, cancellationToken);
            return Ok(new { message = "Test email sent successfully." });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "SMTP test email failed.");
            return Problem(statusCode: StatusCodes.Status502BadGateway, title: "Unable to send test email. Check SMTP settings and server logs.");
        }
    }

    private async Task<SystemSettings> GetOrCreateSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await db.SystemSettings.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings is not null)
            return settings;

        settings = new SystemSettings { Id = 1 };
        db.SystemSettings.Add(settings);
        await db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private static SetupResponse ToResponse(SystemSettings settings) => new(
        settings.SmtpHost, settings.SmtpPort, settings.SmtpUseSsl, settings.SmtpUsername,
        !string.IsNullOrEmpty(settings.SmtpPassword), settings.SmtpFromEmail, settings.SmtpFromName,
        settings.TestRecipientEmail, settings.StaleFileAgeDays, settings.CleanupIntervalHours,
        settings.DeletionGracePeriodDays, settings.UpdatedUtc);

    public sealed record UpdateSetupRequest(
        string SmtpHost,
        int SmtpPort,
        bool SmtpUseSsl,
        string SmtpUsername,
        string SmtpPassword,
        string SmtpFromEmail,
        string SmtpFromName,
        string TestRecipientEmail,
        int StaleFileAgeDays,
        int CleanupIntervalHours,
        int DeletionGracePeriodDays);

    public sealed record TestEmailRequest([Required, EmailAddress, MaxLength(254)] string RecipientEmail);

    public sealed record SetupResponse(
        string SmtpHost,
        int SmtpPort,
        bool SmtpUseSsl,
        string SmtpUsername,
        bool HasSmtpPassword,
        string SmtpFromEmail,
        string SmtpFromName,
        string TestRecipientEmail,
        int StaleFileAgeDays,
        int CleanupIntervalHours,
        int DeletionGracePeriodDays,
        DateTime UpdatedUtc);
}
