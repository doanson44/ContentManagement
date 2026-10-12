using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ContentManagement.Server.Auth;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ContentManagement.UnitTests;

public sealed class ApiKeyAuthenticationHandlerTests
{
    [Fact]
    public async Task Missing_header_returns_no_result()
    {
        var result = await AuthenticateAsync((string[]?)null);

        Assert.False(result.Succeeded);
        Assert.Null(result.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong-key")]
    [InlineData("cm_not-a-client-key")]
    [InlineData("cm_notguid.secret")]
    public async Task Invalid_key_fails_authentication(string suppliedKey)
    {
        var result = await AuthenticateAsync(suppliedKey);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Duplicate_key_headers_fail_authentication()
    {
        var result = await AuthenticateAsync(new[] { "key1", "key2" });

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Valid_managed_client_key_creates_identity_and_distinct_scope_claims()
    {
        var clientId = Guid.NewGuid().ToString("N");
        const string secret = "super-secret-token-12345";
        var clientKey = $"cm_{clientId}.{secret}";

        var result = await AuthenticateAsync(
            [clientKey],
            seedClient: new ApiClient
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                Name = "Test Client",
                SecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
                ScopesJson = JsonSerializer.Serialize(new[] { ScopePolicies.FilesRead, ScopePolicies.JsonWrite, ScopePolicies.FilesRead }),
                IsActive = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);
        Assert.Equal(clientId, result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("Test Client", result.Principal.FindFirstValue(ClaimTypes.Name));
        Assert.Equal(
            new[] { ScopePolicies.FilesRead, ScopePolicies.JsonWrite, ScopePolicies.FilesRead },
            result.Principal.FindAll("scope").Select(claim => claim.Value).ToArray());
    }

    [Fact]
    public async Task Inactive_client_fails_authentication()
    {
        var clientId = Guid.NewGuid().ToString("N");
        const string secret = "super-secret-token-12345";
        var clientKey = $"cm_{clientId}.{secret}";

        var result = await AuthenticateAsync(
            [clientKey],
            seedClient: new ApiClient
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                Name = "Inactive Client",
                SecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
                ScopesJson = "[]",
                IsActive = false,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Wrong_secret_fails_authentication()
    {
        var clientId = Guid.NewGuid().ToString("N");
        var clientKey = $"cm_{clientId}.wrong-secret";

        var result = await AuthenticateAsync(
            [clientKey],
            seedClient: new ApiClient
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                Name = "Test Client",
                SecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("correct-secret"))),
                ScopesJson = "[]",
                IsActive = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    private static Task<AuthenticateResult> AuthenticateAsync(
        string? suppliedKey,
        ApiClient? seedClient = null) =>
        AuthenticateAsync(suppliedKey is null ? null : new[] { suppliedKey }, seedClient);

    private static async Task<AuthenticateResult> AuthenticateAsync(
        string[]? suppliedKeys,
        ApiClient? seedClient = null)
    {
        var dbOptions = new DbContextOptionsBuilder<ContentManagementDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ContentManagementDbContext(dbOptions);
        if (seedClient is not null)
        {
            db.ApiClients.Add(seedClient);
            await db.SaveChangesAsync();
        }

        var handler = new ApiKeyAuthenticationHandler(
            new TestOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            db);

        var context = new DefaultHttpContext();
        if (suppliedKeys is not null)
            context.Request.Headers[ApiKeyAuthenticationDefaults.HeaderName] = suppliedKeys;

        await handler.InitializeAsync(
            new AuthenticationScheme(
                ApiKeyAuthenticationDefaults.Scheme,
                ApiKeyAuthenticationDefaults.Scheme,
                typeof(ApiKeyAuthenticationHandler)),
            context);

        return await handler.AuthenticateAsync();
    }

    private sealed class TestOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
        where TOptions : class
    {
        public TOptions CurrentValue => value;

        public TOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}
