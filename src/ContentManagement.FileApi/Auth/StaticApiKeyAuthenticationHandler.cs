using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ContentManagement.FileApi.Auth;

public static class StaticApiKeyDefaults
{
    public const string Scheme = "StaticApiKey";
    public const string HeaderName = "X-API-Key";
}

public sealed class StaticApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(StaticApiKeyDefaults.HeaderName, out var suppliedValues))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (suppliedValues.Count != 1 || string.IsNullOrWhiteSpace(suppliedValues[0]))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var configuredKey = configuration["Authentication:ApiKey"];
        if (string.IsNullOrWhiteSpace(configuredKey))
            return Task.FromResult(AuthenticateResult.Fail("API key authentication is not configured."));

        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedValues[0]!);
        var expectedBytes = Encoding.UTF8.GetBytes(configuredKey);
        if (suppliedBytes.Length != expectedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "static-api-key-client")],
            StaticApiKeyDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, StaticApiKeyDefaults.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
