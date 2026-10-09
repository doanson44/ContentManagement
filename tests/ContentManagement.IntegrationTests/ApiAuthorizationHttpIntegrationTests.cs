using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using ContentManagement.Server.Auth;
using Xunit;

namespace ContentManagement.IntegrationTests;

public sealed class ApiAuthorizationHttpIntegrationTests
{
    private const string ApiKey = "integration-test-key-with-sufficient-entropy";

    [Fact]
    public async Task Health_endpoint_is_public()
    {
        using var factory = new AuthorizationTestApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_returns_401_without_api_key()
    {
        using var factory = new AuthorizationTestApplicationFactory(ScopePolicies.FilesRead);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/test-protected/files");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_returns_401_for_invalid_api_key()
    {
        using var factory = new AuthorizationTestApplicationFactory(ScopePolicies.FilesRead);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "wrong-key");

        using var response = await client.GetAsync("/api/test-protected/files");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_returns_403_when_key_has_no_required_scope()
    {
        using var factory = new AuthorizationTestApplicationFactory(ScopePolicies.JsonRead);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, ApiKey);

        using var response = await client.GetAsync("/api/test-protected/files");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_returns_200_when_key_has_required_scope()
    {
        using var factory = new AuthorizationTestApplicationFactory(ScopePolicies.FilesRead);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, ApiKey);

        using var response = await client.GetAsync("/api/test-protected/files");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("authorized", await response.Content.ReadAsStringAsync());
    }

    private sealed class AuthorizationTestApplicationFactory(params string[] scopes)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["Authentication:ApiKey"] = ApiKey
                };
                for (var index = 0; index < scopes.Length; index++)
                    settings[$"Authentication:Scopes:{index}"] = scopes[index];

                configuration.AddInMemoryCollection(settings);
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(ProtectedTestController).Assembly);
            });
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
}
