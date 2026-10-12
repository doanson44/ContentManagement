namespace ContentManagement.Server.Compression;

public interface IContentCompressor
{
    CompressedContent CompressJson(string json);
    string DecompressJson(ReadOnlySpan<byte> compressedBytes);
}

public sealed record CompressedContent(byte[] Bytes, long UncompressedSizeBytes, string Sha256);
