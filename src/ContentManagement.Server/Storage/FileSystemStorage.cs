using System.Security.Cryptography;
using ContentManagement.Server.Configuration;
using Microsoft.Extensions.Options;

namespace ContentManagement.Server.Storage;

public sealed class FileSystemStorage(IOptions<ContentManagementOptions> options) : IFileStorage
{
    private readonly string _root = Path.GetFullPath(options.Value.StorageRoot);

    public async Task<StoredBinary> SaveAsync(Stream content, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        var datePath = Path.Combine(DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"), DateTime.UtcNow.ToString("dd"));
        var directory = Path.Combine(_root, datePath);
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        var temp = Path.Combine(directory, id + ".tmp");
        var target = Path.Combine(directory, id + ".bin");
        var key = Path.Combine(datePath, id + ".bin").Replace(Path.DirectorySeparatorChar, '/');
        long size = 0;

        try
        {
            await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            while (true)
            {
                var count = await content.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                size += count;
                if (size > maxBytes) throw new InvalidDataException("File exceeds configured size limit.");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                hash.AppendData(buffer, 0, count);
            }
            await output.FlushAsync(cancellationToken);
            output.Close();
            File.Move(temp, target);
            return new StoredBinary(key, size, Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(storageKey) || Path.IsPathRooted(storageKey) ||
            storageKey.Contains(':') || storageKey.Contains('\\') ||
            storageKey.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Invalid storage key.", nameof(storageKey));

        var path = Path.GetFullPath(Path.Combine(_root, storageKey.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid storage key.", nameof(storageKey));

        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(storageKey) || Path.IsPathRooted(storageKey) ||
            storageKey.Contains(':') || storageKey.Contains('\\') ||
            storageKey.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Invalid storage key.", nameof(storageKey));

        var path = Path.GetFullPath(Path.Combine(_root, storageKey.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid storage key.", nameof(storageKey));
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        return Task.FromResult(stream);
    }
}
