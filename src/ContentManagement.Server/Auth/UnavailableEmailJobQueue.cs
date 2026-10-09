namespace ContentManagement.Server.Auth;

public sealed class UnavailableEmailJobQueue : IEmailJobQueue
{
    private static InvalidOperationException CreateException() =>
        new("Email delivery requires Hangfire:Enabled=true and a configured Hangfire SQL Server connection string.");

    public void EnqueueOtp(string email, string code) => throw CreateException();
    public void EnqueueInvitation(string email, string invitationUrl) => throw CreateException();
    public void EnqueueRegistrationVerification(string email, string verificationUrl) => throw CreateException();
}
