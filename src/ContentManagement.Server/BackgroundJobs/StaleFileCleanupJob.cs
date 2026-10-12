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

        var deletionBefore = now.AddDays(-settings.StaleFileAgeDays);
        var readyForDeletion = await db.Files
            .Where(file =>
                (file.Status == ContentStatus.MarkedForDeletion || file.Status == ContentStatus.Deleting) &&
                file.UpdatedUtc <= deletionBefore)
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
                db.Entry(file).State = EntityState.Modified;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        if (readyForDeletion.Count > 0)
            logger.LogInformation("Stale file cleanup deleted {DeletedCount} of {EligibleCount} eligible marked files.",
                deletedCount, readyForDeletion.Count);
    }
}
