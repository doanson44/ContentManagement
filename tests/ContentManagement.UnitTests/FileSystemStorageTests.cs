using System.Security.Cryptography;
using System.Text;
using ContentManagement.Server.Configuration;
using ContentManagement.Server.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace ContentManagement.UnitTests;

public sealed class FileSystemStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ContentManagementTests", Guid.NewGuid().ToString("N"));
    private readonly FileSystemStorage _storage;

    public FileSystemStorageTests() =>
        _storage = new FileSystemStorage(Options.Create(new ContentManagementOptions { StorageRoot = _root }));

    [Fact]
    public async Task Saves_stream_with_hash_and_reads_it_back()
    {
        var bytes = Encoding.UTF8.GetBytes("sample binary");
        await using var input = new MemoryStream(bytes);
        var stored = await _storage.SaveAsync(input, 1024);
        await using var output = await _storage.OpenReadAsync(stored.StorageKey);
        using var readBack = new MemoryStream();
        await output.CopyToAsync(readBack);
        Assert.Equal(bytes, readBack.ToArray());
        Assert.Equal(bytes.LongLength, stored.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), stored.Sha256);
    }

    [Fact]
    public async Task Rejects_upload_over_limit()
    {
        await using var input = new MemoryStream(new byte[16]);
        await Assert.ThrowsAsync<InvalidDataException>(() => _storage.SaveAsync(input, 8));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
