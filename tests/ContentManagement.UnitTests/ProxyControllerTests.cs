using ContentManagement.Server.Controllers;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ContentManagement.UnitTests;

public sealed class ProxyControllerTests
{
    [Fact]
    public async Task PostAsync_ReturnsConflict_WhenNoProxyIsPinned()
    {
        await using var db = CreateDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = new ProxyController(db, cache, NullLogger<ProxyController>.Instance);

        var result = await controller.PostAsync(new ProxyController.ProxyRequest
        {
            TargetUrl = "https://example.com/",
            Method = "GET",
            TimeoutSeconds = 10,
            CacheDurationSeconds = 60
        }, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public async Task PostAsync_RejectsPrivateTarget_WhenProxyIsPinned(string targetUrl)
    {
        await using var db = CreateDbContext();
        db.PinnedProxies.Add(new PinnedProxy
        {
            Id = 1,
            Address = "http://8.8.8.8:8080",
            Protocol = "http",
            PinnedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = new ProxyController(db, cache, NullLogger<ProxyController>.Instance);

        var result = await controller.PostAsync(new ProxyController.ProxyRequest
        {
            TargetUrl = targetUrl,
            Method = "GET",
            TimeoutSeconds = 10,
            CacheDurationSeconds = 60
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Theory]
    [InlineData("TRACE")]
    [InlineData("CONNECT")]
    [InlineData("BREW")]
    public async Task PostAsync_RejectsUnsupportedMethod(string method)
    {
        await using var db = CreateDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = new ProxyController(db, cache, NullLogger<ProxyController>.Instance);

        var result = await controller.PostAsync(new ProxyController.ProxyRequest
        {
            TargetUrl = "https://example.com/",
            Method = method,
            TimeoutSeconds = 10,
            CacheDurationSeconds = 60
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    private static ContentManagementDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ContentManagementDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}
