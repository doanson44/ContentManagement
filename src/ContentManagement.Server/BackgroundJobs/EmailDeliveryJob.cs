using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentManagement.Server.Auth;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;

namespace ContentManagement.Server.BackgroundJobs;

public sealed class EmailDeliveryJob(
    IEmailSender emailSender,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<EmailDeliveryJob> logger)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("ContentManagement.EmailDelivery.v1");

    [AutomaticRetry(Attempts = 5, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task SendProtectedAsync(string protectedPayload, CancellationToken cancellationToken = default)
    {
        var json = _protector.Unprotect(protectedPayload);
        var payload = JsonSerializer.Deserialize<EmailPayload>(json)
            ?? throw new InvalidOperationException("The queued email payload is invalid.");

        switch (payload.Kind)
        {
            case "otp":
                await emailSender.SendOtpAsync(payload.Email, payload.Value, cancellationToken);
                break;
            case "invitation":
                await emailSender.SendInvitationAsync(payload.Email, payload.Value, cancellationToken);
                break;
            case "registration":
                await emailSender.SendVerificationLinkAsync(payload.Email, payload.Value, cancellationToken);
                break;
            default:
                throw new InvalidOperationException("The queued email kind is unsupported.");
        }

        logger.LogInformation("Delivered queued {EmailKind} email.", payload.Kind);
    }

    public sealed record EmailPayload(string Kind, string Email, string Value);
}
