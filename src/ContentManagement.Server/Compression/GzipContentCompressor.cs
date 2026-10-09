using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContentManagement.Server.Compression;

public sealed class GzipContentCompressor : IContentCompressor
{
    public CompressedContent CompressJson(string json, long maxUncompressedBytes)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (maxUncompressedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxUncompressedBytes));

        using var parsed = JsonDocument.Parse(json);
        var utf8 = Encoding.UTF8.GetBytes(json);
        if (utf8.LongLength > maxUncompressedBytes)
            throw new InvalidDataException("JSON payload exceeds the configured size limit.");

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(utf8);

        return new CompressedContent(output.ToArray(), utf8.LongLength, Convert.ToHexString(SHA256.HashData(utf8)));
    }

    public string DecompressJson(ReadOnlySpan<byte> compressedBytes, long maxUncompressedBytes)
    {
        if (maxUncompressedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxUncompressedBytes));

        using var input = new MemoryStream(compressedBytes.ToArray(), writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = gzip.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            if (output.Length + read > maxUncompressedBytes)
                throw new InvalidDataException("Decompressed JSON exceeds the configured size limit.");
            output.Write(buffer, 0, read);
        }

        var json = new UTF8Encoding(false, true).GetString(output.ToArray());
        using var parsed = JsonDocument.Parse(json);
        return json;
    }
}
