namespace ContentManagement.Server.Auth;

public interface IEmailSender
{
    Task SendOtpAsync(string email, string code, CancellationToken cancellationToken);
    Task SendInvitationAsync(string email, string invitationUrl, CancellationToken cancellationToken);
    Task SendVerificationLinkAsync(string email, string verificationUrl, CancellationToken cancellationToken);
}
