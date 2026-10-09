using System.ComponentModel.DataAnnotations;
using System.Text;
using ContentManagement.Server.Auth;
using ContentManagement.Server.Configuration;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using ContentManagement.Server.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/files")]
public sealed class FilesController(
    ContentManagementDbContext db,
    IFileStorage storage,
    IOptions<ContentManagementOptions> options,
    ILogger<FilesController> logger) : ControllerBase
{
    private const int MaxPageSize = 100;

    [HttpGet]
    [Authorize(Policy = ScopePolicies.FilesRead)]
    public async Task<ActionResult<FileListResponse>> List(
        [FromQuery] string? search, [FromQuery] ContentStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var query = db.Files.AsNoTracking().Where(f => f.Status != ContentStatus.Deleted);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(f => f.FileName.Contains(term) || f.Sha256 == term);
        }
        if (status.HasValue) query = query.Where(f => f.Status == status.Value);
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderByDescending(f => f.CreatedUtc)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(f => new FileListItem(f.Id, f.FileName, f.ContentType, f.SizeBytes, f.Sha256,
                f.Status, f.OwnerId, f.CreatedUtc, f.UpdatedUtc, Convert.ToBase64String(f.RowVersion)))
            .ToListAsync(cancellationToken);
        return Ok(new FileListResponse(items, total, page, pageSize));
    }

    [HttpPost]
    [Authorize(Policy = ScopePolicies.FilesWrite)]
    public async Task<ActionResult<FileDetails>> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length <= 0) return BadRequest(new { message = "Choose a non-empty file." });
        if (file.Length > options.Value.MaxUploadBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { message = "File exceeds configured upload limit." });

        StoredBinary binary;
        await using (var input = file.OpenReadStream())
        {
            try { binary = await storage.SaveAsync(input, options.Value.MaxUploadBytes, cancellationToken); }
            catch (InvalidDataException) { return StatusCode(StatusCodes.Status413PayloadTooLarge); }
        }

        var now = DateTime.UtcNow;
        var entity = new StoredFile
        {
            Id = Guid.NewGuid(),
            FileName = Path.GetFileName((file.FileName ?? string.Empty).Replace('\\\\', '/')).Trim(),
            ContentType = SafeContentType(file.ContentType),
            StorageKey = binary.StorageKey,
            SizeBytes = binary.SizeBytes,
            Sha256 = binary.Sha256,
            CreatedUtc = now,
            UpdatedUtc = now,
            Status = ContentStatus.Active,
            OwnerId = User.FindFirst("sub")?.Value
        };
        if (string.IsNullOrWhiteSpace(entity.FileName))
        {
            await storage.DeleteAsync(binary.StorageKey, cancellationToken);
            return BadRequest(new { message = "Invalid file name." });
        }

        try
        {
            db.Files.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            try { await storage.DeleteAsync(binary.StorageKey, cancellationToken); }
            catch (Exception cleanupException) { logger.LogError(cleanupException, "Failed to clean up orphaned binary after metadata persistence failure."); }
            throw;
        }
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, ToDetails(entity));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = ScopePolicies.FilesRead)]
    public async Task<ActionResult<FileDetails>> Get(Guid id, CancellationToken cancellationToken)
    {
        var file = await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (file is null || file.Status is ContentStatus.MarkedForDeletion or ContentStatus.Deleting or ContentStatus.Deleted)
            return NotFound();
        return Ok(ToDetails(file));
    }

    [HttpGet("{id:guid}/content")]
    [Authorize(Policy = ScopePolicies.FilesRead)]
    public async Task<IActionResult> Content(Guid id, CancellationToken cancellationToken)
    {
        var file = await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (file is null || file.Status != ContentStatus.Active) return NotFound();
        try
        {
            var stream = await storage.OpenReadAsync(file.StorageKey, cancellationToken);
            Response.Headers.ContentDisposition = $"inline; filename*=UTF-8''{Uri.EscapeDataString(file.FileName)}";
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(stream, SafeContentType(file.ContentType), enableRangeProcessing: true);
        }
        catch (FileNotFoundException) { return NotFound(); }
    }

    [HttpGet("{id:guid}/download")]
    [Authorize(Policy = ScopePolicies.FilesRead)]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var file = await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (file is null || file.Status != ContentStatus.Active) return NotFound();
        try { return File(await storage.OpenReadAsync(file.StorageKey, cancellationToken), SafeContentType(file.ContentType), file.FileName, enableRangeProcessing: true); }
        catch (FileNotFoundException) { return NotFound(); }
    }

    [HttpPatch("{id:guid}")]
    [Authorize(Policy = ScopePolicies.FilesWrite)]
    public async Task<IActionResult> Rename(Guid id, RenameFileRequest request, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(request.FileName?.Trim());
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Any(char.IsControl))
            return BadRequest(new { message = "File name must be 1–255 characters and contain no control characters." });
        var file = await db.Files.SingleOrDefaultAsync(f => f.Id == id && f.Status == ContentStatus.Active, cancellationToken);
        if (file is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(request.RowVersion) &&
            !Convert.ToBase64String(file.RowVersion).Equals(request.RowVersion, StringComparison.Ordinal))
            return Conflict(new { message = "File metadata changed. Refresh and retry." });
        file.FileName = name;
        file.UpdatedUtc = DateTime.UtcNow;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { message = "File metadata changed. Refresh and retry." }); }
        return Ok(ToDetails(file));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = ScopePolicies.FilesDelete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var file = await db.Files.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (file is null) return NoContent();
        if (file.Status == ContentStatus.Active)
        {
            file.Status = ContentStatus.MarkedForDeletion;
            file.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        return Accepted(new { file.Id, file.Status });
    }

    private static string SafeContentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || value.Any(char.IsControl))
            return "application/octet-stream";
        // Do not permit active HTML/SVG content to execute in the authenticated application origin.
        return value.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase)
            ? "application/octet-stream" : value;
    }

    private static FileDetails ToDetails(StoredFile file) => new(file.Id, file.FileName, file.ContentType,
        file.SizeBytes, file.Sha256, file.Status, file.OwnerId, file.CreatedUtc, file.UpdatedUtc,
        Convert.ToBase64String(file.RowVersion));

    public sealed record FileListItem(Guid Id, string FileName, string ContentType, long SizeBytes,
        string Sha256, ContentStatus Status, string? OwnerId, DateTime CreatedUtc, DateTime UpdatedUtc, string RowVersion);
    public sealed record FileListResponse(IReadOnlyList<FileListItem> Items, long Total, int Page, int PageSize);
    public sealed record FileDetails(Guid Id, string FileName, string ContentType, long SizeBytes,
        string Sha256, ContentStatus Status, string? OwnerId, DateTime CreatedUtc, DateTime UpdatedUtc, string RowVersion);
    public sealed record RenameFileRequest([Required, StringLength(255)] string FileName, string? RowVersion);
}
