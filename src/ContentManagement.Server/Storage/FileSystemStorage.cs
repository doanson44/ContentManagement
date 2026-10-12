using System.Security.Cryptography;
using System.Linq;
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


public interface IVideoPreviewService
{
    Task<string> GetWmvPreviewPathAsync(string storageKey, string sha256, CancellationToken cancellationToken = default);
}

public sealed class FfmpegVideoPreviewService(
    IFileStorage storage,
    IOptions<ContentManagementOptions> options,
    ILogger<FfmpegVideoPreviewService> logger) : IVideoPreviewService
{
    private static readonly SemaphoreSlim ConversionGate = new(1, 1);
    private readonly string _cacheRoot = Path.GetFullPath(
        options.Value.PreviewCacheRoot ?? Path.Combine(options.Value.StorageRoot, ".previews"));
    private readonly string _ffmpegPath = string.IsNullOrWhiteSpace(options.Value.FfmpegPath)
        ? "ffmpeg"
        : options.Value.FfmpegPath;

    public async Task<string> GetWmvPreviewPathAsync(string storageKey, string sha256, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("A valid SHA-256 hash is required.", nameof(sha256));

        Directory.CreateDirectory(_cacheRoot);
        var outputPath = Path.Combine(_cacheRoot, sha256.ToLowerInvariant() + ".mp4");
        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
            return outputPath;

        await ConversionGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                return outputPath;

            var inputPath = Path.Combine(_cacheRoot, Guid.NewGuid().ToString("N") + ".wmv.tmp");
            var partialOutputPath = Path.Combine(_cacheRoot, Guid.NewGuid().ToString("N") + ".mp4.tmp");
            try
            {
                await using (var source = await storage.OpenReadAsync(storageKey, cancellationToken))
                await using (var target = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(target, cancellationToken);
                }

                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    }
                };
                foreach (var argument in new[]
                {
                    "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                    "-i", inputPath, "-map", "0:v:0", "-map", "0:a?",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
                    "-pix_fmt", "yuv420p", "-threads", "2",
                    "-c:a", "aac", "-b:a", "128k",
                    "-movflags", "+faststart", "-f", "mp4", partialOutputPath
                })
                    process.StartInfo.ArgumentList.Add(argument);

                if (!process.Start())
                    throw new InvalidOperationException("FFmpeg could not be started.");

                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    if (cancellationToken.IsCancellationRequested) throw;
                    throw new TimeoutException("WMV preview conversion exceeded the two-minute limit.");
                }

                var error = await stderrTask;
                _ = await stdoutTask;
                if (process.ExitCode != 0 || !File.Exists(partialOutputPath) || new FileInfo(partialOutputPath).Length == 0)
                {
                    logger.LogWarning("FFmpeg WMV preview conversion failed with exit code {ExitCode}: {Error}",
                        process.ExitCode, error.Length > 2000 ? error[..2000] : error);
                    throw new InvalidOperationException("WMV preview conversion failed. Verify FFmpeg is installed and the source codec is supported.");
                }

                File.Move(partialOutputPath, outputPath, overwrite: true);
                PruneCache(outputPath, options.Value.MaxPreviewCacheBytes);
                return outputPath;
            }
            finally
            {
                TryDelete(inputPath);
                TryDelete(partialOutputPath);
            }
        }
        finally
        {
            ConversionGate.Release();
        }
    }

    private void PruneCache(string currentPath, long maxCacheBytes)
    {
        if (maxCacheBytes <= 0) return;
        try
        {
            var files = new DirectoryInfo(_cacheRoot).GetFiles("*.mp4")
                .OrderBy(file => file.LastAccessTimeUtc)
                .ToList();
            var total = files.Sum(file => file.Length);
            foreach (var file in files)
            {
                if (total <= maxCacheBytes) break;
                if (string.Equals(file.FullName, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
