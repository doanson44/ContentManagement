using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContentManagement.IntegrationTests.Infrastructure;
using ContentManagement.Server.Auth;
using ContentManagement.Server.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ContentManagement.IntegrationTests;

[Collection(Collections.Integration)]
public sealed class JsonDocumentsApiIntegrationTests(SqlServerFixture db)
{
    private const string ApiKey = "json-integration-test-key-with-sufficient-entropy";
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Json_endpoints_require_authentication()
    {
        using var factory = CreateFactory([]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var list = await client.GetAsync("/api/json");
        using var create = await client.PostAsJsonAsync("/api/json", new CreateJsonDocumentRequest("test", "{\"ok\":true}"));

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
    }

    [Fact]
    public async Task Json_endpoints_enforce_required_scopes()
    {
        using var factory = CreateFactory([ScopePolicies.JsonRead]);
        using var client = CreateClient(factory);

        using var list = await client.GetAsync("/api/json");
        using var create = await client.PostAsJsonAsync("/api/json", new CreateJsonDocumentRequest("test", "{\"ok\":true}"));
        using var delete = await client.DeleteAsync($"/api/json/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task Json_document_can_be_created_read_listed_updated_and_soft_deleted()
    {
        using var factory = CreateFactory([ScopePolicies.JsonRead, ScopePolicies.JsonWrite, ScopePolicies.JsonDelete]);
        using var client = CreateClient(factory);
        var type = $"integration-{Guid.NewGuid():N}";
        const string originalJson = "{\"enabled\":true,\"count\":1}";
        const string updatedJson = "{\"enabled\":false,\"count\":2}";

        using var create = await client.PostAsJsonAsync("/api/json", new CreateJsonDocumentRequest(type, originalJson));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonDocumentDetails>(WebJsonOptions);
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(type, created.DocumentType);
        Assert.Equal(originalJson, created.Json);
        Assert.Equal(originalJson.Length, created.UncompressedSizeBytes);
        Assert.True(created.CompressedSizeBytes > 0);
        Assert.Equal(64, created.Sha256.Length);
        Assert.Equal("Active", created.Status.ToString());
        Assert.False(string.IsNullOrWhiteSpace(created.RowVersion));

        using var get = await client.GetAsync($"/api/json/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var read = await get.Content.ReadFromJsonAsync<JsonDocumentDetails>();
        Assert.Equal(originalJson, read?.Json);

        using var list = await client.GetAsync($"/api/json?search={Uri.EscapeDataString(type)}&page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(originalJson, listBody, StringComparison.Ordinal);
        var listing = JsonSerializer.Deserialize<JsonDocumentListResponse>(listBody, WebJsonOptions);
        Assert.NotNull(listing);
        Assert.Contains(listing.Items, item => item.Id == created.Id);

        using var update = await client.PutAsJsonAsync($"/api/json/{created.Id}", new UpdateJsonDocumentRequest(type + "-updated", updatedJson));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<JsonDocumentDetails>();
        Assert.Equal(type + "-updated", updated?.DocumentType);
        Assert.Equal(updatedJson, updated?.Json);
        Assert.Equal(updatedJson.Length, updated?.UncompressedSizeBytes);

        using var delete = await client.DeleteAsync($"/api/json/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var getAfterDelete = await client.GetAsync($"/api/json/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getAfterDelete.StatusCode);

        using var updateAfterDelete = await client.PutAsJsonAsync($"/api/json/{created.Id}", new UpdateJsonDocumentRequest(type, originalJson));
        Assert.Equal(HttpStatusCode.NotFound, updateAfterDelete.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_malformed_json()
    {
        using var factory = CreateFactory([ScopePolicies.JsonWrite]);
        using var client = CreateClient(factory);

        using var response = await client.PostAsJsonAsync("/api/json", new CreateJsonDocumentRequest($"invalid-{Guid.NewGuid():N}", "{not-json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_payload_over_configured_size_limit()
    {
        using var factory = CreateFactory([ScopePolicies.JsonWrite], maxJsonDocumentBytes: 32);
        using var client = CreateClient(factory);

        using var response = await client.PostAsJsonAsync("/api/json", new CreateJsonDocumentRequest($"large-{Guid.NewGuid():N}", "{\"value\":\"" + new string('x', 128) + "\"}"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task List_clamps_page_size_and_filters_by_status()
    {
        using var factory = CreateFactory([ScopePolicies.JsonRead, ScopePolicies.JsonWrite]);
        using var client = CreateClient(factory);
        var type = $"paging-{Guid.NewGuid():N}";
        using var create = await client.PostAsJsonAsync("/api/json", new CreateJsonDocumentRequest(type, "{\"ok\":true}"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var response = await client.GetAsync($"/api/json?search={Uri.EscapeDataString(type)}&page=0&pageSize=1000&status=Active");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonDocumentListResponse>(WebJsonOptions);
        Assert.NotNull(result);
        Assert.Equal(1, result.Page);
        Assert.Equal(100, result.PageSize);
        Assert.All(result.Items, item => Assert.Equal("Active", item.Status.ToString()));
    }

    private WebApplicationFactory<Program> CreateFactory(string[] scopes, long maxJsonDocumentBytes = 10_485_760) =>
        new JsonApiTestApplicationFactory(ApiKey, scopes, db.ConnectionString, maxJsonDocumentBytes);

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, ApiKey);
        return client;
    }

    private sealed class JsonApiTestApplicationFactory(
        string apiKey, string[] scopes, string connectionString, long maxJsonDocumentBytes)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:ContentManagement", connectionString);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["Authentication:ApiKey"] = apiKey,
                    ["ConnectionStrings:ContentManagement"] = connectionString,
                    ["Hangfire:Enabled"] = "false",
                    ["ContentManagement:MaxJsonDocumentBytes"] = maxJsonDocumentBytes.ToString()
                };
                for (var index = 0; index < scopes.Length; index++)
                    settings[$"Authentication:Scopes:{index}"] = scopes[index];
                configuration.AddInMemoryCollection(settings);
            });
        }
    }
}
