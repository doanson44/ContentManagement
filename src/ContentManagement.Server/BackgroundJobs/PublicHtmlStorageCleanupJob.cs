using ContentManagement.Server.Data;
using ContentManagement.Server.Storage;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.BackgroundJobs;

public sealed class PublicHtmlStorageCleanupJob(
    ContentManagementDbContext db,
    IFileStorage storage,
    ILogger<PublicHtmlStorageCleanupJob> logger)
{
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var ids = await db.PublicHtmlPages.AsNoTracking()
            .Where(page => page.IsDeleted && page.StorageDeletePending)
            .OrderBy(page => page.UpdatedUtc).Take(100)
            .Select(page => page.Id).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var page = await db.PublicHtmlPages.SingleOrDefaultAsync(
                    item => item.Id == id && item.IsDeleted && item.StorageDeletePending, cancellationToken);
                if (page is null) continue;
                await storage.DeleteAsync(page.StorageKey, cancellationToken);
                page.StorageDeletePending = false;
                page.UpdatedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Failed to clean up deleted public HTML binary for page {PageId}; retrying later.", id);
                db.ChangeTracker.Clear();
            }
        }
    }
}
