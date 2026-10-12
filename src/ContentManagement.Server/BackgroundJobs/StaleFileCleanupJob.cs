using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using ContentManagement.Server.Storage;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.BackgroundJobs;

public sealed class StaleFileCleanupJob(
    ContentManagementDbContext db,
    IFileStorage fileStorage,
    ILogger<StaleFileCleanupJob> logger)
{
    private sealed record StaleFile(Guid Id, string StorageKey);

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var settings = await db.SystemSettings.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings is null)
            return;

        var deletionBefore = now.AddDays(-settings.StaleFileAgeDays);
        var readyForDeletion = await db.Files
            .AsNoTracking()
            .Where(file =>
                file.Status == ContentStatus.MarkedForDeletion &&
                file.UpdatedUtc <= deletionBefore)
            .OrderBy(file => file.UpdatedUtc)
            .Take(100)
            .Select(file => new StaleFile(file.Id, file.StorageKey))
            .ToListAsync(cancellationToken);

        var deletedCount = 0;
        foreach (var file in readyForDeletion)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Delete the binary first. Only remove the SQL record after the storage
                // deletion succeeds, so a failed file delete cannot orphan metadata.
                await fileStorage.DeleteAsync(file.StorageKey, cancellationToken);

                var affectedRows = await db.Files
                    .Where(current => current.Id == file.Id &&
                        current.Status == ContentStatus.MarkedForDeletion &&
                        current.UpdatedUtc <= deletionBefore)
                    .ExecuteDeleteAsync(cancellationToken);

                if (affectedRows == 1)
                    deletedCount++;
                else
                    logger.LogInformation(
                        "Skipped metadata deletion for stale file {FileId} because its state changed during cleanup.",
                        file.Id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception,
                    "Failed to hard-delete stale file {FileId}; cleanup will retry on the next scheduled run.",
                    file.Id);
            }
        }

        if (readyForDeletion.Count > 0)
            logger.LogInformation("Stale file cleanup hard-deleted {DeletedCount} of {EligibleCount} eligible marked files.",
                deletedCount, readyForDeletion.Count);
    }
}
