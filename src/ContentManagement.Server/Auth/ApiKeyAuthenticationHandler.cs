using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ContentManagement.Server.Auth;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<ApiKeyOptions> apiKeyOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    private readonly ApiKeyOptions _apiKeyOptions = apiKeyOptions.Value;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationDefaults.HeaderName, out StringValues suppliedValues))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (suppliedValues.Count != 1 ||
            string.IsNullOrWhiteSpace(_apiKeyOptions.ApiKey) ||
            !FixedTimeEquals(suppliedValues[0], _apiKeyOptions.ApiKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "static-api-client"),
            new(ClaimTypes.Name, "Static API client")
        };

        foreach (var scope in _apiKeyOptions.Scopes
                     .Where(scope => !string.IsNullOrWhiteSpace(scope))
                     .Distinct(StringComparer.Ordinal))
        {
            claims.Add(new Claim("scope", scope));
        }

        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, ApiKeyAuthenticationDefaults.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool FixedTimeEquals(string? supplied, string configured)
    {
        if (string.IsNullOrEmpty(supplied))
            return false;

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, configuredHash);
    }
}
