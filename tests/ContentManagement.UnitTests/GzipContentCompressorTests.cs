using System.Text;
using ContentManagement.Server.Compression;
using Xunit;

namespace ContentManagement.UnitTests;

public sealed class GzipContentCompressorTests
{
    private readonly GzipContentCompressor _compressor = new();

    [Fact]
    public void Compresses_valid_json_and_calculates_hash_of_original_utf8()
    {
        const string json = "{\"message\":\"xin chào\",\"count\":3}";
        var compressed = _compressor.CompressJson(json);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), compressed.UncompressedSizeBytes);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json))), compressed.Sha256);
        Assert.Equal(json, _compressor.DecompressJson(compressed.Bytes));
    }

    [Fact]
    public void Rejects_malformed_json() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => _compressor.CompressJson("{bad"));

    [Fact]
    public void Compresses_and_decompresses_json_larger_than_previous_default_limit()
    {
        var json = "{\"payload\":\"" + new string('x', 10 * 1024 * 1024 + 1) + "\"}";
        var compressed = _compressor.CompressJson(json);

        Assert.Equal(Encoding.UTF8.GetByteCount(json), compressed.UncompressedSizeBytes);
        Assert.Equal(json, _compressor.DecompressJson(compressed.Bytes));
    }
}
