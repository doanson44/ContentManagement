using System.Text.Json;
using ContentManagement.Server.BackgroundJobs;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;

namespace ContentManagement.Server.Auth;

public sealed class HangfireEmailJobQueue(
    IBackgroundJobClient backgroundJobs,
    IDataProtectionProvider dataProtectionProvider) : IEmailJobQueue
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("ContentManagement.EmailDelivery.v1");

    public void EnqueueOtp(string email, string code) => Enqueue("otp", email, code);

    public void EnqueueInvitation(string email, string invitationUrl) => Enqueue("invitation", email, invitationUrl);

    public void EnqueueRegistrationVerification(string email, string verificationUrl) => Enqueue("registration", email, verificationUrl);

    private void Enqueue(string kind, string email, string value)
    {
        var payload = JsonSerializer.Serialize(new EmailDeliveryJob.EmailPayload(kind, email, value));
        var protectedPayload = _protector.Protect(payload);
        backgroundJobs.Enqueue<EmailDeliveryJob>(job =>
            job.SendProtectedAsync(protectedPayload, CancellationToken.None));
    }
}
