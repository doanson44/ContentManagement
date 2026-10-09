using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace ContentManagement.Server.Auth;

public sealed class SmtpEmailSender(
    IOptions<SmtpOptions> options,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public Task SendOtpAsync(string email, string code, CancellationToken cancellationToken) =>
        SendAsync(email, "Your ContentManagement sign-in code",
            $"Your one-time sign-in code is {code}. It expires in 10 minutes. If you did not request this code, you can ignore this email.",
            cancellationToken);

    private async Task SendAsync(string email, string subject, string body, CancellationToken cancellationToken)
    {
        var configured = options.Value;

        if (string.IsNullOrWhiteSpace(configured.Host) ||
            !MailAddress.TryCreate(configured.FromEmail, out var fromAddress))
            throw new InvalidOperationException("SMTP host and a valid sender email must be configured.");

        using var message = new MailMessage
        {
            From = new MailAddress(fromAddress.Address, configured.FromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        message.To.Add(new MailAddress(email));

        using var client = new SmtpClient(configured.Host, configured.Port)
        {
            EnableSsl = configured.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = string.IsNullOrEmpty(configured.Username)
                ? null
                : new NetworkCredential(configured.Username, configured.Password)
        };
        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, cancellationToken);
        logger.LogInformation("Sent ContentManagement email message.");
    }
}
