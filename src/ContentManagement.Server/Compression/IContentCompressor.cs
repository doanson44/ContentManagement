namespace ContentManagement.Server.Compression;

public interface IContentCompressor
{
    CompressedContent CompressJson(string json, long maxUncompressedBytes);
    string DecompressJson(ReadOnlySpan<byte> compressedBytes, long maxUncompressedBytes);
}

public sealed record CompressedContent(byte[] Bytes, long UncompressedSizeBytes, string Sha256);
