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
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var settings = await db.SystemSettings.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings is null)
            return;

        if (settings.LastCleanupUtc is not null &&
            now - settings.LastCleanupUtc.Value < TimeSpan.FromHours(settings.CleanupIntervalHours))
            return;

        settings.LastCleanupUtc = now;
        var staleBefore = now.AddDays(-settings.StaleFileAgeDays);
        var staleFiles = await db.Files
            .Where(file => file.Status == ContentStatus.Active && file.CreatedUtc <= staleBefore)
            .OrderBy(file => file.CreatedUtc)
            .Take(500)
            .ToListAsync(cancellationToken);

        foreach (var file in staleFiles)
        {
            file.Status = ContentStatus.MarkedForDeletion;
            file.UpdatedUtc = now;
        }
        await db.SaveChangesAsync(cancellationToken);

        var deletionBefore = now.AddDays(-settings.DeletionGracePeriodDays);
        var readyForDeletion = await db.Files
            .Where(file => file.Status == ContentStatus.MarkedForDeletion && file.UpdatedUtc <= deletionBefore)
            .OrderBy(file => file.UpdatedUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        var deletedCount = 0;
        foreach (var file in readyForDeletion)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                file.Status = ContentStatus.Deleting;
                await db.SaveChangesAsync(cancellationToken);

                await fileStorage.DeleteAsync(file.StorageKey, cancellationToken);
                db.Files.Remove(file);
                await db.SaveChangesAsync(cancellationToken);
                deletedCount++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Failed to clean stale file {FileId}; it will be retried after the grace period.", file.Id);
                file.Status = ContentStatus.MarkedForDeletion;
                file.UpdatedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        if (staleFiles.Count > 0 || deletedCount > 0)
            logger.LogInformation("Stale file cleanup marked {MarkedCount} files and deleted {DeletedCount} files.",
                staleFiles.Count, deletedCount);
    }
}
