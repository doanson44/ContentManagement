using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using ContentManagement.Server.Storage;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.BackgroundJobs;

/// <summary>
/// Reconciles filesystem binaries against SQL metadata. Only old .bin files are eligible,
/// so in-flight uploads have a grace period to finish committing metadata.
/// </summary>
public sealed class OrphanFileCleanupJob(
    ContentManagementDbContext db,
    IFileStorage fileStorage,
    IFileStorageInventory localInventory,
    ExternalFileApiClient externalFiles,
    ILogger<OrphanFileCleanupJob> logger)
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);
    private const int MaxDeletesPerProviderPerRun = 500;

    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var olderThanUtc = DateTime.UtcNow.Subtract(GracePeriod);
        var localKeys = await db.Files.AsNoTracking()
            .Where(file => file.UploadSource == FileUploadSource.Local)
            .Select(file => file.StorageKey)
            .ToListAsync(cancellationToken);
        var localKeySet = localKeys.ToHashSet(StringComparer.Ordinal);
        var localCandidates = localInventory.ListOrphanCandidates(olderThanUtc)
            .Where(item => !localKeySet.Contains(item.StorageKey))
            .Take(MaxDeletesPerProviderPerRun)
            .ToList();

        var localDeleted = 0;
        foreach (var candidate in localCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await fileStorage.DeleteAsync(candidate.StorageKey, cancellationToken);
                localDeleted++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Failed to remove orphan local binary {StorageKey}.", candidate.StorageKey);
            }
        }

        logger.LogInformation(
            "Local orphan cleanup removed {DeletedCount} of {CandidateCount} candidate binaries.",
            localDeleted, localCandidates.Count);

        try
        {
            var externalKeys = await db.Files.AsNoTracking()
                .Where(file => file.UploadSource == FileUploadSource.ExternalServer)
                .Select(file => file.StorageKey)
                .ToListAsync(cancellationToken);
            var externalKeySet = externalKeys.ToHashSet(StringComparer.Ordinal);
            var externalCandidates = await externalFiles.ListConfiguredFilesAsync(olderThanUtc, cancellationToken);
            var orphanCandidates = externalCandidates
                .Where(item => !externalKeySet.Contains(item.FilePath))
                .Take(MaxDeletesPerProviderPerRun)
                .ToList();

            var externalDeleted = 0;
            foreach (var candidate in orphanCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await externalFiles.DeleteConfiguredFileAsync(candidate.FilePath, cancellationToken);
                    externalDeleted++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Failed to remove orphan external binary {StorageKey}.", candidate.FilePath);
                }
            }

            logger.LogInformation(
                "External orphan cleanup removed {DeletedCount} of {CandidateCount} candidate binaries.",
                externalDeleted, orphanCandidates.Count);
        }
        catch (InvalidOperationException exception)
        {
            // External storage is optional; local cleanup should still run when it is not configured.
            logger.LogInformation(exception, "External orphan cleanup skipped because External File API is not configured.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogError(exception, "External orphan cleanup could not enumerate External File API storage; no external files were deleted.");
        }
    }
}
