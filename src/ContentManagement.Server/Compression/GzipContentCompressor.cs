using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContentManagement.Server.Compression;

public sealed class GzipContentCompressor : IContentCompressor
{
    public CompressedContent CompressJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var parsed = JsonDocument.Parse(json);
        var utf8 = Encoding.UTF8.GetBytes(json);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(utf8);

        return new CompressedContent(output.ToArray(), utf8.LongLength, Convert.ToHexString(SHA256.HashData(utf8)));
    }

    public string DecompressJson(ReadOnlySpan<byte> compressedBytes)
    {
        using var input = new MemoryStream(compressedBytes.ToArray(), writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);

        var json = new UTF8Encoding(false, true).GetString(output.ToArray());
        using var parsed = JsonDocument.Parse(json);
        return json;
    }
}
