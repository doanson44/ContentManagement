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
        var compressed = _compressor.CompressJson(json, 1024);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), compressed.UncompressedSizeBytes);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json))), compressed.Sha256);
        Assert.Equal(json, _compressor.DecompressJson(compressed.Bytes, 1024));
    }

    [Fact]
    public void Rejects_malformed_json() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => _compressor.CompressJson("{bad", 1024));

    [Fact]
    public void Rejects_payload_over_limit_on_compress_and_decompress()
    {
        var compressed = _compressor.CompressJson("{\"value\":\"long\"}", 100);
        Assert.Throws<InvalidDataException>(() => _compressor.CompressJson("{\"value\":\"long\"}", 5));
        Assert.Throws<InvalidDataException>(() => _compressor.DecompressJson(compressed.Bytes, 5));
    }
}
