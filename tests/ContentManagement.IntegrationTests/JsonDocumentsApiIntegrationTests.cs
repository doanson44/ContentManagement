using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContentManagement.IntegrationTests.Infrastructure;
using ContentManagement.Server.Auth;
using ContentManagement.Server.Controllers;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ContentManagement.IntegrationTests;

[Collection(Collections.Integration)]
public sealed class JsonDocumentsApiIntegrationTests(SqlServerFixture db)
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Json_endpoints_require_authentication()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var list = await client.GetAsync("/api/json");
        using var create = await client.PostAsJsonAsync("/api/json", new CreateJsonDocumentRequest(null, "test", "{\"ok\":true}"));

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
    }

    [Fact]
    public async Task Json_endpoints_enforce_required_scopes()
    {
        using var factory = CreateFactory();
        using var client = await CreateClientAsync(factory, [ScopePolicies.JsonRead]);

        using var list = await client.GetAsync("/api/json");
        using var create = await client.PostAsJsonAsync("/api/json", new CreateJsonDocumentRequest(null, "test", "{\"ok\":true}"));
        using var delete = await client.DeleteAsync($"/api/json/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task Json_document_can_be_created_read_listed_updated_and_permanently_deleted()
    {
        using var factory = CreateFactory();
        using var client = await CreateClientAsync(factory, [ScopePolicies.JsonRead, ScopePolicies.JsonWrite, ScopePolicies.JsonDelete]);
        var type = $"integration-{Guid.NewGuid():N}";
        const string originalJson = "{\"enabled\":true,\"count\":1}";
        const string updatedJson = "{\"enabled\":false,\"count\":2}";

        using var create = await client.PostAsJsonAsync("/api/json", new CreateJsonDocumentRequest("manual-code-" + Guid.NewGuid().ToString("N"), type, originalJson));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonDocumentDetails>(WebJsonOptions);
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(type, created.DocumentType);
        Assert.StartsWith("manual-code-", created.Code);
        Assert.Equal(originalJson, created.Json);
        Assert.Equal(originalJson.Length, created.UncompressedSizeBytes);
        Assert.True(created.CompressedSizeBytes > 0);
        Assert.Equal(64, created.Sha256.Length);
        Assert.Equal("Active", created.Status.ToString());
        Assert.False(string.IsNullOrWhiteSpace(created.RowVersion));

        using var get = await client.GetAsync($"/api/json/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var read = await get.Content.ReadFromJsonAsync<JsonDocumentDetails>(WebJsonOptions);
        Assert.Equal(originalJson, read?.Json);

        using var list = await client.GetAsync($"/api/json?search={Uri.EscapeDataString(type)}&page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(originalJson, listBody, StringComparison.Ordinal);
        var listing = JsonSerializer.Deserialize<JsonDocumentListResponse>(listBody, WebJsonOptions);
        Assert.NotNull(listing);
        Assert.Contains(listing.Items, item => item.Id == created.Id);

        using var update = await client.PutAsJsonAsync($"/api/json/{created.Id}", new UpdateJsonDocumentRequest(created.Code, type + "-updated", updatedJson));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<JsonDocumentDetails>(WebJsonOptions);
        Assert.Equal(type + "-updated", updated?.DocumentType);
        Assert.Equal(updatedJson, updated?.Json);
        Assert.Equal(updatedJson.Length, updated?.UncompressedSizeBytes);

        using var delete = await client.DeleteAsync($"/api/json/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var getAfterDelete = await client.GetAsync($"/api/json/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getAfterDelete.StatusCode);

        using var updateAfterDelete = await client.PutAsJsonAsync($"/api/json/{created.Id}", new UpdateJsonDocumentRequest(created.Code, type, originalJson));
        Assert.Equal(HttpStatusCode.NotFound, updateAfterDelete.StatusCode);
    }

    [Fact]
    public async Task Create_generates_code_when_blank_and_rejects_duplicate_custom_code()
    {
        using var factory = CreateFactory();
        using var client = await CreateClientAsync(factory, [ScopePolicies.JsonRead, ScopePolicies.JsonWrite]);
        var code = $"integration-{Guid.NewGuid():N}";

        using var first = await client.PostAsJsonAsync("/api/json", new CreateJsonDocumentRequest(code, "code-test", "{\"value\":1}"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<JsonDocumentDetails>(WebJsonOptions);
        Assert.NotNull(created);
        Assert.Equal(code, created.Code);

        using var duplicate = await client.PostAsJsonAsync("/api/json", new CreateJsonDocumentRequest(code, "code-test-duplicate", "{\"value\":2}"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var generated = await client.PostAsJsonAsync("/api/json", new CreateJsonDocumentRequest(null, "auto-code-test", "{\"value\":3}"));
        Assert.Equal(HttpStatusCode.Created, generated.StatusCode);
        var autoCode = await generated.Content.ReadFromJsonAsync<JsonDocumentDetails>(WebJsonOptions);
        Assert.NotNull(autoCode);
        Assert.StartsWith("json-", autoCode.Code);
        Assert.NotEqual(code, autoCode.Code);
    }

    [Fact]
    public async Task Create_rejects_malformed_json()
    {
        using var factory = CreateFactory();
        using var client = await CreateClientAsync(factory, [ScopePolicies.JsonWrite]);

        using var response = await client.PostAsJsonAsync("/api/json", new CreateJsonDocumentRequest(null, $"invalid-{Guid.NewGuid():N}", "{not-json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_accepts_json_larger_than_previous_default_limit()
    {
        using var factory = CreateFactory();
        using var client = await CreateClientAsync(factory, [ScopePolicies.JsonWrite, ScopePolicies.JsonRead]);
        var type = $"large-{Guid.NewGuid():N}";
        var json = "{\"payload\":\"" + new string('x', 10 * 1024 * 1024 + 1) + "\"}";

        using var response = await client.PostAsJsonAsync("/api/json", new CreateJsonDocumentRequest(null, type, json));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonDocumentDetails>(WebJsonOptions);
        Assert.NotNull(created);
        Assert.Equal(json.Length, created.UncompressedSizeBytes);
        Assert.Equal(json, created.Json);
    }

    [Fact]
    public async Task List_clamps_page_size_and_filters_by_status()
    {
        using var factory = CreateFactory();
        using var client = await CreateClientAsync(factory, [ScopePolicies.JsonRead, ScopePolicies.JsonWrite]);
        var type = $"paging-{Guid.NewGuid():N}";
        using var create = await client.PostAsJsonAsync("/api/json", new CreateJsonDocumentRequest(null, type, "{\"ok\":true}"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var response = await client.GetAsync($"/api/json?search={Uri.EscapeDataString(type)}&page=0&pageSize=1000&status=Active");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonDocumentListResponse>(WebJsonOptions);
        Assert.NotNull(result);
        Assert.Equal(1, result.Page);
        Assert.Equal(100, result.PageSize);
        Assert.All(result.Items, item => Assert.Equal("Active", item.Status.ToString()));
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new JsonApiTestApplicationFactory(db.ConnectionString);

    private async Task<HttpClient> CreateClientAsync(WebApplicationFactory<Program> factory, string[] scopes)
    {
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
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = "Integration test client",
            SecretHash = secretHash,
            ScopesJson = JsonSerializer.Serialize(scopes),
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, $"cm_{clientId}.{secret}");
        return client;
    }

    private sealed class JsonApiTestApplicationFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
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
        }
    }
}
