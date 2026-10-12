using System.Security.Cryptography;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using ContentManagement.FileApi.Configuration;

namespace ContentManagement.FileApi.Storage;

public sealed record StoredFileInfo(string FilePath, long SizeBytes, string Sha256);

public sealed class FileSizeLimitExceededException(long maximumBytes)
    : Exception($"File exceeds the configured upload limit of {maximumBytes} bytes.");

public sealed class FileStorageService
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private readonly string _rootPath;
    private readonly string _rootPrefix;
    private readonly long _maxUploadBytes;

    public FileStorageService(IOptions<FileApiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        if (string.IsNullOrWhiteSpace(value.RootPath))
            throw new InvalidOperationException("FileApi:RootPath must be configured.");
        if (value.MaxUploadBytes <= 0)
            throw new InvalidOperationException("FileApi:MaxUploadBytes must be greater than zero.");

        _rootPath = Path.GetFullPath(value.RootPath);
        Directory.CreateDirectory(_rootPath);
        _rootPrefix = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;
        _maxUploadBytes = value.MaxUploadBytes;
    }

    public async Task<StoredFileInfo> SaveAsync(
        string filePath,
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var destinationPath = ResolvePath(filePath);
        var directory = Path.GetDirectoryName(destinationPath)!;
        EnsureSafeExistingDirectories(directory);
        Directory.CreateDirectory(directory);
        EnsureSafeExistingDirectories(directory);

        var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.uploading");
        long totalBytes = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var bytesRead = await source.ReadAsync(buffer, cancellationToken);
                    if (bytesRead == 0)
                        break;

                    totalBytes = checked(totalBytes + bytesRead);
                    if (totalBytes > _maxUploadBytes)
                        throw new FileSizeLimitExceededException(_maxUploadBytes);

                    hash.AppendData(buffer, 0, bytesRead);
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }

                if (totalBytes == 0)
                    throw new InvalidDataException("Empty files are not accepted.");

                await destination.FlushAsync(cancellationToken);
            }

            EnsureSafeExistingDirectories(directory);
            try
            {
                File.Move(temporaryPath, destinationPath);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                throw new FileAlreadyExistsException();
            }

            return new StoredFileInfo(filePath, totalBytes, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Preserve the original upload error; a stale temporary file can be removed operationally.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the original upload error; a stale temporary file can be removed operationally.
            }
        }
    }

    public string GetExistingFilePath(string filePath)
    {
        var fullPath = ResolvePath(filePath);
        EnsureSafeExistingDirectories(Path.GetDirectoryName(fullPath)!);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The requested file does not exist.");

        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Symbolic links and reparse points are not allowed.");

        return fullPath;
    }

    public string GetContentType(string filePath)
    {
        return ContentTypes.TryGetContentType(filePath, out var contentType)
            ? contentType
            : "application/octet-stream";
    }

    public static bool CanViewInline(string contentType)
    {
        if (contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            return true;

        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return !contentType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);

        return contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
               contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
               contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase) ||
               contentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolvePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new InvalidDataException("filePath is required.");

        var normalized = filePath.Replace('\\', '/');
        if (normalized.StartsWith('/') ||
            Path.IsPathRooted(filePath) ||
            normalized.Contains(':'))
        {
            throw new InvalidDataException("filePath must be relative to the configured storage root.");
        }

        var segments = normalized.Split('/');
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.Any(character => char.IsControl(character) || "<>:\"|?*".Contains(character)) ||
                segment.EndsWith(' ') || segment.EndsWith('.')))
        {
            throw new InvalidDataException("filePath contains an invalid path segment.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, Path.Combine(segments)));
        if (!fullPath.StartsWith(_rootPrefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new InvalidDataException("filePath must remain inside the configured storage root.");
        }

        return fullPath;
    }

    private void EnsureSafeExistingDirectories(string directory)
    {
        var relative = Path.GetRelativePath(_rootPath, directory);
        var current = _rootPath;
        if (Directory.Exists(current) &&
            (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The configured storage root cannot be a symbolic link or reparse point.");
        }

        if (relative == ".")
            return;

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Symbolic links and reparse points are not allowed in file paths.");
            }
        }
    }
}
