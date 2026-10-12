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
        var eligibleIds = await db.Files
            .AsNoTracking()
            .Where(file =>
                file.Status == ContentStatus.MarkedForDeletion &&
                file.UpdatedUtc <= deletionBefore)
            .OrderBy(file => file.UpdatedUtc)
            .Take(100)
            .Select(file => file.Id)
            .ToListAsync(cancellationToken);

        var deletedCount = 0;
        foreach (var fileId in eligibleIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Lock the SQL row while deleting the binary and metadata. This prevents
                // Restore from reactivating a file after its binary has been removed.
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var file = await db.Files
                    .FromSqlInterpolated($"SELECT * FROM [Files] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {fileId}")
                    .SingleOrDefaultAsync(cancellationToken);

                if (file is null ||
                    file.Status != ContentStatus.MarkedForDeletion ||
                    file.UpdatedUtc > deletionBefore)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    continue;
                }

                // File deletion is idempotent: if the process fails before SQL commits,
                // the next run can safely retry even when the binary is already absent.
                await fileStorage.DeleteAsync(file.StorageKey, cancellationToken);
                db.Files.Remove(file);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                deletedCount++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception,
                    "Failed to hard-delete stale file {FileId}; cleanup will retry on the next scheduled run.",
                    fileId);
                db.ChangeTracker.Clear();
            }
        }

        if (eligibleIds.Count > 0)
            logger.LogInformation("Stale file cleanup hard-deleted {DeletedCount} of {EligibleCount} eligible marked files.",
                deletedCount, eligibleIds.Count);
    }
}
