using System.Security.Claims;
using System.Text.Encodings.Web;
using ContentManagement.Server.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ContentManagement.UnitTests;

public sealed class ApiKeyAuthenticationHandlerTests
{
    private const string ConfiguredKey = "test-key-with-sufficient-entropy";

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
    public async Task Invalid_key_fails_authentication(string suppliedKey)
    {
        var result = await AuthenticateAsync(suppliedKey);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Duplicate_key_headers_fail_authentication()
    {
        var result = await AuthenticateAsync(new[] { ConfiguredKey, ConfiguredKey });

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Valid_key_creates_identity_and_distinct_scope_claims()
    {
        var result = await AuthenticateAsync(
            new[] { ConfiguredKey },
            ScopePolicies.FilesRead,
            ScopePolicies.JsonWrite,
            ScopePolicies.FilesRead);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);
        Assert.Equal("static-api-client", result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(
            new[] { ScopePolicies.FilesRead, ScopePolicies.JsonWrite },
            result.Principal.FindAll("scope").Select(claim => claim.Value).ToArray());
    }

    private static Task<AuthenticateResult> AuthenticateAsync(
        string? suppliedKey,
        params string[] scopes) =>
        AuthenticateAsync(suppliedKey is null ? null : new[] { suppliedKey }, scopes);

    private static async Task<AuthenticateResult> AuthenticateAsync(
        string[]? suppliedKeys,
        params string[] scopes)
    {
        var options = new ApiKeyOptions
        {
            ApiKey = ConfiguredKey,
            Scopes = scopes.ToList()
        };
        var handler = new ApiKeyAuthenticationHandler(
            new TestOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            new TestOptionsMonitor<ApiKeyOptions>(options));

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
