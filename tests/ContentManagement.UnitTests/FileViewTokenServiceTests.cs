using ContentManagement.Server.Configuration;
using ContentManagement.Server.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace ContentManagement.UnitTests;

public sealed class FileViewTokenServiceTests
{
    [Fact]
    public void Created_view_url_contains_file_specific_expiring_token()
    {
        var service = CreateService();
        var fileId = Guid.NewGuid();

        var url = service.CreateRelativeUrl(fileId);

        Assert.StartsWith("https://content.example.com/api/files/view/", url);
        var token = url[(url.LastIndexOf('/') + 1)..];
        Assert.True(service.TryValidate(token, out var resolvedId));
        Assert.Equal(fileId, resolvedId);
    }

    [Fact]
    public void Tampered_view_token_is_rejected()
    {
        var service = CreateService();
        var url = service.CreateRelativeUrl(Guid.NewGuid());
        var token = url[(url.LastIndexOf('/') + 1)..];
        var tampered = token[..^1] + (token[^1] == 'A' ? "B" : "A");

        Assert.False(service.TryValidate(tampered, out _));
    }

    private static FileViewTokenService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:ApiKey"] = "unit-test-key-that-is-not-a-production-secret"
            })
            .Build();
        return new FileViewTokenService(
            configuration,
            Options.Create(new ContentManagementOptions { PublicBaseUrl = "https://content.example.com" }));
    }
}
