using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentManagement.IntegrationTests.Infrastructure;
using ContentManagement.Server.Auth;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ContentManagement.IntegrationTests;

[Collection(Collections.Integration)]
public sealed class ManagedFileStorageIntegrationTests(SqlServerFixture db)
{
    [Fact]
    public async Task Managed_clients_have_isolated_namespaces_and_owner_metadata()
    {
        var (keyA, idA) = await SeedClientAsync();
        var (keyB, idB) = await SeedClientAsync();
        var root = Path.Combine(Path.GetTempPath(), "ContentManagement-IntegrationTests", Guid.NewGuid().ToString("N"));
        using var factory = CreateFactory(root);
        using var clientA = CreateClient(factory, keyA);
        using var clientB = CreateClient(factory, keyB);
        const string path = "shared/document.txt";

        using var uploadA = await UploadAsync(clientA, path, "A");
        using var uploadB = await UploadAsync(clientB, path, "B");
        Assert.Equal(HttpStatusCode.Created, uploadA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, uploadB.StatusCode);

        using var jsonA = await JsonDocument.ParseAsync(await uploadA.Content.ReadAsStreamAsync());
        using var jsonB = await JsonDocument.ParseAsync(await uploadB.Content.ReadAsStreamAsync());
        var fileAId = jsonA.RootElement.GetProperty("id").GetGuid();
        var fileBId = jsonB.RootElement.GetProperty("id").GetGuid();

        using var ownPath = await clientA.GetAsync("/api/files/by-path?path=" + Uri.EscapeDataString(path));
        using var foreignNamespace = await clientA.GetAsync("/api/files/by-path?path=" + Uri.EscapeDataString("clients/" + idB + "/" + path));
        using var crossId = await clientA.GetAsync("/api/files/" + fileBId);
        Assert.Equal(HttpStatusCode.OK, ownPath.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignNamespace.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossId.StatusCode);

        var options = new DbContextOptionsBuilder<ContentManagementDbContext>().UseSqlServer(db.ConnectionString).Options;
        await using var context = new ContentManagementDbContext(options);
        var fileA = await context.Files.SingleAsync(file => file.Id == fileAId);
        var fileB = await context.Files.SingleAsync(file => file.Id == fileBId);
        Assert.Equal(FileOwnerType.Client, fileA.Type);
        Assert.Equal(FileOwnerType.Client, fileB.Type);
        Assert.Equal(idA, fileA.OwnerId);
        Assert.Equal(idB, fileB.OwnerId);
        Assert.Equal($"clients/{idA}/{path}", fileA.FilePath);
        Assert.Equal($"clients/{idB}/{path}", fileB.FilePath);
    }

    [Fact]
    public async Task Override_replaces_existing_content_and_delete_by_path_marks_file()
    {
        var (key, _) = await SeedClientAsync();
        var root = Path.Combine(Path.GetTempPath(), "ContentManagement-IntegrationTests", Guid.NewGuid().ToString("N"));
        using var factory = CreateFactory(root);
        using var client = CreateClient(factory, key);
        const string path = "documents/file.txt";

        using var first = await UploadAsync(client, path, "old");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var duplicate = await UploadAsync(client, path, "duplicate");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var overwrite = await UploadAsync(client, path, "new", true);
        Assert.Equal(HttpStatusCode.OK, overwrite.StatusCode);

        using var body = await client.GetAsync($"/api/files/by-path/content?path={Uri.EscapeDataString(path)}");
        Assert.Equal("new", await body.Content.ReadAsStringAsync());

        using var deleted = await client.DeleteAsync($"/api/files/by-path?path={Uri.EscapeDataString(path)}");
        Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);
        using var missing = await client.GetAsync($"/api/files/by-path?path={Uri.EscapeDataString(path)}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory(string root) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ContentManagement", db.ConnectionString);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:ContentManagement"] = db.ConnectionString,
                    ["ContentManagement:StorageRoot"] = root,
                    ["Hangfire:Enabled"] = "false"
                }));
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory, string key)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, key);
        return client;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string path, string value, bool replace = false)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "file.txt");
        form.Add(new StringContent(path), "filePath");
        if (replace) form.Add(new StringContent("true"), "override");
        return await client.PostAsync("/api/files", form);
    }

    private async Task<(string Key, string ClientId)> SeedClientAsync()
    {
        var clientId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var options = new DbContextOptionsBuilder<ContentManagementDbContext>().UseSqlServer(db.ConnectionString).Options;
        await using var context = new ContentManagementDbContext(options);
        context.ApiClients.Add(new ApiClient
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = "Managed file storage test",
            SecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
            ScopesJson = JsonSerializer.Serialize(new[] { ScopePolicies.FilesRead, ScopePolicies.FilesWrite, ScopePolicies.FilesDelete }),
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        return ($"cm_{clientId}.{secret}", clientId);
    }
}
