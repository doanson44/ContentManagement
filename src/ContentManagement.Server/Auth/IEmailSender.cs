namespace ContentManagement.Server.Auth;

public interface IEmailSender
{
    Task SendOtpAsync(string email, string code, CancellationToken cancellationToken);
    Task SendTestAsync(string email, CancellationToken cancellationToken);
}
