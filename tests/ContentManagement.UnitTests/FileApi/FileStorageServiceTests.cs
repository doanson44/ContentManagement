using System.Security.Cryptography;
using System.Text;
using ContentManagement.FileApi.Configuration;
using ContentManagement.FileApi.Storage;
using Microsoft.Extensions.Options;

namespace ContentManagement.UnitTests.FileApi;

public sealed class FileStorageServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"ContentManagement.FileApi.Tests.{Guid.NewGuid():N}");
    private readonly FileStorageService _storage;

    public FileStorageServiceTests()
    {
        _storage = new FileStorageService(Options.Create(new FileApiOptions
        {
            RootPath = _rootPath,
            MaxUploadBytes = 1024
        }));
    }

    [Fact]
    public async Task SaveAsync_WritesNestedFileAndReturnsSha256()
    {
        var content = Encoding.UTF8.GetBytes("file contents");
        await using var source = new MemoryStream(content);

        var result = await _storage.SaveAsync("images/sample.txt", source, CancellationToken.None);

        Assert.Equal("images/sample.txt", result.FilePath);
        Assert.Equal(content.Length, result.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), result.Sha256);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_rootPath, "images", "sample.txt")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("images/../../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData(@"C:\outside.txt")]
    [InlineData("images//empty-segment.txt")]
    public async Task SaveAsync_RejectsPathsOutsideOrInvalidInsideRoot(string filePath)
    {
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("contents"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _storage.SaveAsync(filePath, source, CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_DoesNotOverwriteExistingFile()
    {
        await using var first = new MemoryStream(Encoding.UTF8.GetBytes("first"));
        await using var second = new MemoryStream(Encoding.UTF8.GetBytes("second"));
        await _storage.SaveAsync("sample.txt", first, CancellationToken.None);

        await Assert.ThrowsAsync<FileAlreadyExistsException>(
            () => _storage.SaveAsync("sample.txt", second, CancellationToken.None));

        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(_rootPath, "sample.txt")));
    }

    [Fact]
    public async Task SaveAsync_RejectsOversizedFileAndRemovesPartialFile()
    {
        var storage = new FileStorageService(Options.Create(new FileApiOptions
        {
            RootPath = _rootPath,
            MaxUploadBytes = 3
        }));
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("four"));

        await Assert.ThrowsAsync<FileSizeLimitExceededException>(
            () => storage.SaveAsync("too-large.txt", source, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_rootPath, "too-large.txt")));
        Assert.Empty(Directory.GetFiles(_rootPath, "*.uploading", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("application/pdf", true)]
    [InlineData("text/plain", true)]
    [InlineData("text/html", false)]
    [InlineData("image/svg+xml", false)]
    [InlineData("application/octet-stream", false)]
    public void CanViewInline_OnlyAllowsSafeBrowserContentTypes(string contentType, bool expected)
    {
        Assert.Equal(expected, FileStorageService.CanViewInline(contentType));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }
}
