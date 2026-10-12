using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ContentManagement.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ContentManagement.Server.Auth;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<ApiKeyOptions> apiKeyOptions,
    ContentManagementDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    private readonly ApiKeyOptions _apiKeyOptions = apiKeyOptions.Value;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationDefaults.HeaderName, out StringValues suppliedValues))
            return AuthenticateResult.NoResult();

        if (suppliedValues.Count != 1 || string.IsNullOrWhiteSpace(suppliedValues[0]))
            return AuthenticateResult.Fail("Invalid API key.");

        var suppliedKey = suppliedValues[0]!;
        if (!string.IsNullOrWhiteSpace(_apiKeyOptions.ApiKey) &&
            FixedTimeEquals(suppliedKey, _apiKeyOptions.ApiKey))
        {
            return AuthenticateResult.Success(CreateTicket(
                "static-api-client", "Static API client", _apiKeyOptions.Scopes));
        }

        // Managed client keys have the format cm_<client-id>.<secret>. The database
        // stores only the SHA-256 hash of the randomly generated secret.
        if (!suppliedKey.StartsWith("cm_", StringComparison.Ordinal))
            return AuthenticateResult.Fail("Invalid API key.");

        var separator = suppliedKey.IndexOf('.', 3);
        if (separator <= 3 || separator == suppliedKey.Length - 1)
            return AuthenticateResult.Fail("Invalid API key.");

        var clientId = suppliedKey[3..separator];
        var secret = suppliedKey[(separator + 1)..];
        if (!Guid.TryParseExact(clientId, "N", out _))
            return AuthenticateResult.Fail("Invalid API key.");

        var client = await db.ApiClients.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ClientId == clientId && item.IsActive, Context.RequestAborted);
        if (client is null)
            return AuthenticateResult.Fail("Invalid API key.");

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        byte[] expectedHash;
        try { expectedHash = Convert.FromHexString(client.SecretHash); }
        catch (FormatException) { return AuthenticateResult.Fail("Invalid API key."); }

        if (!CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash))
            return AuthenticateResult.Fail("Invalid API key.");

        string[] scopes;
        try { scopes = JsonSerializer.Deserialize<string[]>(client.ScopesJson) ?? []; }
        catch (JsonException) { return AuthenticateResult.Fail("Invalid API client configuration."); }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, client.ClientId),
            new(ClaimTypes.Name, client.Name),
            new("client_id", client.ClientId)
        };
        claims.AddRange(scopes.Select(scope => new Claim("scope", scope)));

        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationDefaults.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(identity), ApiKeyAuthenticationDefaults.Scheme));
    }

    private static AuthenticationTicket CreateTicket(string id, string name, IEnumerable<string> scopes)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id),
            new(ClaimTypes.Name, name)
        };
        claims.AddRange(scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.Ordinal)
            .Select(scope => new Claim("scope", scope)));

        return new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyAuthenticationDefaults.Scheme)),
            ApiKeyAuthenticationDefaults.Scheme);
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
