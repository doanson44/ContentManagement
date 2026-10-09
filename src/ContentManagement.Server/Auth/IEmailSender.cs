namespace ContentManagement.Server.Auth;

public interface IEmailSender
{
    Task SendOtpAsync(string email, string code, CancellationToken cancellationToken);
}
