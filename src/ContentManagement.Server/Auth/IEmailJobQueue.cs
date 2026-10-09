namespace ContentManagement.Server.Auth;

public interface IEmailJobQueue
{
    void EnqueueOtp(string email, string code);
    void EnqueueInvitation(string email, string invitationUrl);
    void EnqueueRegistrationVerification(string email, string verificationUrl);
}
