using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
public sealed class PublicHtmlPagesIntegrationTests(SqlServerFixture db)
{
    [Fact]
    public async Task Public_html_lifecycle_uploads_publishes_renames_and_deletes_safely()
    {
        var root = Path.Combine(Path.GetTempPath(), "ContentManagementTests", Guid.NewGuid().ToString("N"));
        using var factory = CreateFactory(root);
        using var anonymous = factory.CreateClient();
        using var anonymousList = await anonymous.GetAsync("/api/public-html");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousList.StatusCode);
        using var missing = await anonymous.GetAsync("/html/not-found");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var client = await CreateClientAsync(factory, [ScopePolicies.FilesRead, ScopePolicies.FilesWrite, ScopePolicies.FilesDelete]);
        var slug = $"public-{Guid.NewGuid():N}";
        const string html = "<!doctype html><html><body><h1>Public test content</h1></body></html>";
        using var form = new MultipartFormDataContent();
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(html));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        form.Add(content, "file", "public-test.html"); form.Add(new StringContent(slug), "slug"); form.Add(new StringContent("Public test page"), "title");
        using var upload = await client.PostAsync("/api/public-html", form);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var created = await upload.Content.ReadFromJsonAsync<PublicHtmlDetails>();
        Assert.NotNull(created);
        Assert.Equal(slug, created.Slug); Assert.False(created.IsPublished); Assert.Equal((long)Encoding.UTF8.GetByteCount(html), created.SizeBytes);
        Assert.Equal(64, created.Sha256.Length);

        using var unpublished = await anonymous.GetAsync($"/html/{slug}");
        Assert.Equal(HttpStatusCode.NotFound, unpublished.StatusCode);
        using var duplicateForm = new MultipartFormDataContent();
        using var duplicateContent = new ByteArrayContent(Encoding.UTF8.GetBytes(html));
        duplicateContent.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        duplicateForm.Add(duplicateContent, "file", "duplicate.html"); duplicateForm.Add(new StringContent(slug), "slug"); duplicateForm.Add(new StringContent("Duplicate"), "title");
        using var duplicate = await client.PostAsync("/api/public-html", duplicateForm);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var publish = await client.PutAsJsonAsync($"/api/public-html/{created.Id}/publish", new { isPublished = true });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        using var wrapper = await anonymous.GetAsync($"/html/{slug}");
        Assert.Equal(HttpStatusCode.OK, wrapper.StatusCode);
        var wrapperHtml = await wrapper.Content.ReadAsStringAsync();
        Assert.Contains("iframe", wrapperHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sandbox=", wrapperHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("referrerpolicy=\"no-referrer\"", wrapperHtml, StringComparison.OrdinalIgnoreCase);
        using var publicContent = await anonymous.GetAsync($"/api/public-html/{slug}/content");
        Assert.Equal(HttpStatusCode.OK, publicContent.StatusCode);
        Assert.Equal(html, await publicContent.Content.ReadAsStringAsync());
        var contentCsp = Assert.Single(publicContent.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src https: http: data: blob: 'unsafe-inline'", contentCsp, StringComparison.Ordinal);
        Assert.Contains("style-src https: http: data: blob: 'unsafe-inline'", contentCsp, StringComparison.Ordinal);
        Assert.DoesNotContain("allow-same-origin", contentCsp, StringComparison.Ordinal);

        var newSlug = $"renamed-{Guid.NewGuid():N}";
        using var rename = await client.PatchAsJsonAsync($"/api/public-html/{created.Id}/slug", new { slug = newSlug });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        using var oldUrl = await anonymous.GetAsync($"/html/{slug}");
        Assert.Equal(HttpStatusCode.NotFound, oldUrl.StatusCode);
        using var newUrl = await anonymous.GetAsync($"/html/{newSlug}");
        Assert.Equal(HttpStatusCode.OK, newUrl.StatusCode);

        using var delete = await client.DeleteAsync($"/api/public-html/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var afterDelete = await anonymous.GetAsync($"/html/{newSlug}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
        await using var context = new ContentManagementDbContext(new DbContextOptionsBuilder<ContentManagementDbContext>().UseSqlServer(db.ConnectionString).Options);
        var deleted = await context.PublicHtmlPages.SingleAsync(item => item.Id == created.Id);
        Assert.True(deleted.IsDeleted); Assert.False(deleted.IsPublished); Assert.False(deleted.StorageDeletePending);
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Public_html_write_operations_require_write_scope()
    {
        using var factory = CreateFactory(Path.Combine(Path.GetTempPath(), "ContentManagementTests", Guid.NewGuid().ToString("N")));
        using var client = await CreateClientAsync(factory, [ScopePolicies.FilesRead]);
        using var publish = await client.PutAsJsonAsync($"/api/public-html/{Guid.NewGuid()}/publish", new { isPublished = true });
        using var delete = await client.DeleteAsync($"/api/public-html/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, publish.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory(string storageRoot) => new PublicHtmlTestApplicationFactory(db.ConnectionString, storageRoot);

    private async Task<HttpClient> CreateClientAsync(WebApplicationFactory<Program> factory, string[] scopes)
    {
        var clientId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await using var context = new ContentManagementDbContext(new DbContextOptionsBuilder<ContentManagementDbContext>().UseSqlServer(db.ConnectionString).Options);
        context.ApiClients.Add(new ApiClient
        {
            Id = Guid.NewGuid(), ClientId = clientId, Name = "Public HTML integration test",
            SecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
            ScopesJson = JsonSerializer.Serialize(scopes), IsActive = true, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, $"cm_{clientId}.{secret}");
        return client;
    }

    private sealed class PublicHtmlTestApplicationFactory(string connectionString, string storageRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:ContentManagement", connectionString);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ContentManagement"] = connectionString,
                ["ContentManagement:StorageRoot"] = storageRoot,
                ["Hangfire:Enabled"] = "false"
            }));
        }
    }

    private sealed record PublicHtmlDetails(Guid Id, string Title, string Slug, long SizeBytes, string Sha256,
        bool IsPublished, DateTime? PublishedAtUtc, DateTime? LastViewedAtUtc, DateTime CreatedUtc,
        DateTime UpdatedUtc, bool StorageDeletePending);
}
