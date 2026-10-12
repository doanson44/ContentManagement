using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentManagement.IntegrationTests.Infrastructure;
using ContentManagement.Server.Controllers;
using ContentManagement.Server.Auth;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ContentManagement.IntegrationTests;

[Collection(Collections.Integration)]
public sealed class ApiAuthorizationHttpIntegrationTests(SqlServerFixture db)
{
    [Fact]
    public async Task Health_endpoint_is_public()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("Healthy", health?.Status);
    }

    [Fact]
    public async Task Readiness_endpoint_reports_sql_server_healthy()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", (await response.Content.ReadAsStringAsync()).Trim());
    }

    [Fact]
    public async Task Unsafe_request_without_origin_is_rejected()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Unsafe_request_from_same_origin_passes_origin_guard()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var response = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unsafe_request_from_untrusted_origin_is_rejected()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "https://attacker.example");

        using var response = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_returns_401_without_api_key()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/test-protected/files");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_returns_401_for_invalid_api_key()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "wrong-key");

        using var response = await client.GetAsync("/api/test-protected/files");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_returns_403_when_key_has_no_required_scope()
    {
        var (apiKey, _) = await SeedManagedClientAsync([ScopePolicies.JsonRead], isActive: true);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, apiKey);

        using var response = await client.GetAsync("/api/test-protected/files");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_returns_200_when_key_has_required_scope()
    {
        var (apiKey, _) = await SeedManagedClientAsync([ScopePolicies.FilesRead], isActive: true);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, apiKey);

        using var response = await client.GetAsync("/api/test-protected/files");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("authorized", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Managed_api_client_authenticates_with_its_assigned_scope()
    {
        var (apiKey, _) = await SeedManagedClientAsync([ScopePolicies.FilesRead], isActive: true);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, apiKey);

        using var response = await client.GetAsync("/api/test-protected/files");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("authorized", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Managed_api_client_without_required_scope_is_forbidden()
    {
        var (apiKey, _) = await SeedManagedClientAsync([ScopePolicies.JsonRead], isActive: true);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, apiKey);

        using var response = await client.GetAsync("/api/test-protected/files");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_managed_api_client_cannot_authenticate()
    {
        var (apiKey, _) = await SeedManagedClientAsync([ScopePolicies.FilesRead], isActive: false);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, apiKey);

        using var response = await client.GetAsync("/api/test-protected/files");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<(string ApiKey, Guid Id)> SeedManagedClientAsync(string[] scopes, bool isActive)
    {
        var id = Guid.NewGuid();
        var clientId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var secretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        var options = new DbContextOptionsBuilder<ContentManagementDbContext>()
            .UseSqlServer(db.ConnectionString)
            .Options;

        await using var context = new ContentManagementDbContext(options);
        context.ApiClients.Add(new ApiClient
        {
            Id = id,
            ClientId = clientId,
            Name = "Integration test client",
            SecretHash = secretHash,
            ScopesJson = JsonSerializer.Serialize(scopes),
            IsActive = isActive,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        return ($"cm_{clientId}.{secret}", id);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private WebApplicationFactory<Program> CreateFactory() =>
        new AuthorizationTestApplicationFactory(db.ConnectionString);

    private sealed class AuthorizationTestApplicationFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Point the app at the Testcontainers SQL Server instance.
            builder.UseSetting("ConnectionStrings:ContentManagement", connectionString);

            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:ContentManagement"] = connectionString,
                    ["Hangfire:Enabled"] = "false"
                };

                configuration.AddInMemoryCollection(settings);
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(ProtectedTestController).Assembly);
            });
        }
    }
}

[ApiController]
[Route("api/test-protected")]
public sealed class ProtectedTestController : ControllerBase
{
    [HttpGet("files")]
    [Authorize(Policy = ScopePolicies.FilesRead)]
    public IActionResult GetFiles() => Ok("authorized");
}
