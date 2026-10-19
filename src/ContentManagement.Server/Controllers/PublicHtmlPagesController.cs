using System.ComponentModel.DataAnnotations;
using System.Text;
using ContentManagement.Server.Auth;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using ContentManagement.Server.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/public-html")]
public sealed class PublicHtmlPagesController(
    ContentManagementDbContext db,
    IFileStorage storage,
    ILogger<PublicHtmlPagesController> logger) : ControllerBase
{
    private const long MaxHtmlBytes = 5L * 1024 * 1024;
    private const int MaxPageSize = 100;

    [HttpGet]
    [Authorize(Policy = ScopePolicies.FilesRead)]
    public async Task<ActionResult<PublicHtmlListResponse>> List(
        [FromQuery] string? search, [FromQuery] string? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var all = db.PublicHtmlPages.AsNoTracking().Where(item => !item.IsDeleted);
        var published = await all.LongCountAsync(item => item.IsPublished, cancellationToken);
        var unpublished = await all.LongCountAsync(item => !item.IsPublished, cancellationToken);
        var query = all;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(item => item.Title.Contains(term) || item.Slug.Contains(term));
        }
        if (string.Equals(status, "published", StringComparison.OrdinalIgnoreCase))
            query = query.Where(item => item.IsPublished);
        else if (string.Equals(status, "unpublished", StringComparison.OrdinalIgnoreCase))
            query = query.Where(item => !item.IsPublished);

        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderByDescending(item => item.CreatedUtc)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new PublicHtmlListItem(item.Id, item.Title, item.Slug, item.SizeBytes,
                item.IsPublished, item.PublishedAtUtc, item.LastViewedAtUtc, item.CreatedUtc, item.UpdatedUtc))
            .ToListAsync(cancellationToken);
        return Ok(new PublicHtmlListResponse(rows, total, published, unpublished, page, pageSize));
    }

    [HttpPost]
    [Authorize(Policy = ScopePolicies.FilesWrite)]
    [RequestSizeLimit(MaxHtmlBytes + 64 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<PublicHtmlPageDetails>> Upload(
        [FromForm] UploadPublicHtmlRequest request, CancellationToken cancellationToken)
    {
        if (request.File is null || request.File.Length <= 0 || request.File.Length > MaxHtmlBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { code = "PUBLIC_HTML_INVALID_SIZE", message = "HTML files must be between 1 byte and 5 MB." });

        var extension = Path.GetExtension(Path.GetFileName(request.File.FileName ?? string.Empty));
        if (!extension.Equals(".html", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { code = "PUBLIC_HTML_INVALID_INPUT", message = "Only .html and .htm files are accepted." });

        var title = string.IsNullOrWhiteSpace(request.Title)
            ? Path.GetFileNameWithoutExtension(request.File.FileName).Trim()
            : request.Title.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200 || title.Any(char.IsControl))
            return BadRequest(new { code = "PUBLIC_HTML_INVALID_INPUT", message = "Title must be 1–200 characters and contain no control characters." });
        if (!PublicHtmlSlug.TryNormalize(request.Slug, out var slug))
            return BadRequest(new { code = "PUBLIC_HTML_INVALID_INPUT", message = "Invalid slug." });
        if (await db.PublicHtmlPages.AnyAsync(item => item.Slug == slug && !item.IsDeleted, cancellationToken))
            return Conflict(new { code = "PUBLIC_HTML_SLUG_EXISTS", message = "This slug is already in use." });

        StoredBinary binary;
        await using (var input = request.File.OpenReadStream())
        {
            try { binary = await storage.SaveAsync(input, MaxHtmlBytes, cancellationToken); }
            catch (InvalidDataException)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge,
                    new { code = "PUBLIC_HTML_INVALID_SIZE", message = "HTML files must be no larger than 5 MB." });
            }
        }

        var now = DateTime.UtcNow;
        var page = new PublicHtmlPage
        {
            Id = Guid.NewGuid(), Title = title, Slug = slug, StorageKey = binary.StorageKey,
            SizeBytes = binary.SizeBytes, Sha256 = binary.Sha256, IsPublished = false,
            CreatedUtc = now, UpdatedUtc = now,
            OwnerId = User.FindFirst("sub")?.Value ?? User.Identity?.Name
        };
        try
        {
            db.PublicHtmlPages.Add(page);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            try { await storage.DeleteAsync(binary.StorageKey, CancellationToken.None); }
            catch (Exception cleanupException) { logger.LogError(cleanupException, "Unable to clean up public HTML upload after metadata persistence failed."); }
            if (await db.PublicHtmlPages.AsNoTracking().AnyAsync(item => item.Slug == slug && !item.IsDeleted, cancellationToken))
                return Conflict(new { code = "PUBLIC_HTML_SLUG_EXISTS", message = "This slug is already in use." });
            throw;
        }
        return CreatedAtAction(nameof(GetById), new { id = page.Id }, ToDetails(page));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = ScopePolicies.FilesRead)]
    public async Task<ActionResult<PublicHtmlPageDetails>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var page = await db.PublicHtmlPages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && !item.IsDeleted, cancellationToken);
        return page is null ? NotFound() : Ok(ToDetails(page));
    }

    [HttpPatch("{id:guid}/slug")]
    [Authorize(Policy = ScopePolicies.FilesWrite)]
    public async Task<ActionResult<PublicHtmlPageDetails>> Rename(Guid id, RenamePublicHtmlSlugRequest request, CancellationToken cancellationToken)
    {
        if (!PublicHtmlSlug.TryNormalize(request.Slug, out var slug))
            return BadRequest(new { code = "PUBLIC_HTML_INVALID_INPUT", message = "Invalid slug." });
        var page = await db.PublicHtmlPages.SingleOrDefaultAsync(item => item.Id == id && !item.IsDeleted, cancellationToken);
        if (page is null) return NotFound();
        if (await db.PublicHtmlPages.AnyAsync(item => item.Id != id && item.Slug == slug && !item.IsDeleted, cancellationToken))
            return Conflict(new { code = "PUBLIC_HTML_SLUG_EXISTS", message = "This slug is already in use." });
        page.Slug = slug;
        page.UpdatedUtc = DateTime.UtcNow;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { return Conflict(new { code = "PUBLIC_HTML_SLUG_EXISTS", message = "This slug is already in use." }); }
        return Ok(ToDetails(page));
    }

    [HttpPut("{id:guid}/publish")]
    [Authorize(Policy = ScopePolicies.FilesWrite)]
    public async Task<ActionResult<PublicHtmlPageDetails>> SetPublished(Guid id, SetPublicHtmlPublishedRequest request, CancellationToken cancellationToken)
    {
        var page = await db.PublicHtmlPages.SingleOrDefaultAsync(item => item.Id == id && !item.IsDeleted, cancellationToken);
        if (page is null) return NotFound();
        page.IsPublished = request.IsPublished;
        page.PublishedAtUtc = request.IsPublished ? DateTime.UtcNow : null;
        page.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(ToDetails(page));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = ScopePolicies.FilesDelete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var page = await db.PublicHtmlPages.SingleOrDefaultAsync(item => item.Id == id && !item.IsDeleted, cancellationToken);
        if (page is null) return NotFound();
        page.IsPublished = false;
        page.PublishedAtUtc = null;
        page.IsDeleted = true;
        page.StorageDeletePending = true;
        page.Slug = $"deleted-{page.Id:N}";
        page.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            await storage.DeleteAsync(page.StorageKey, cancellationToken);
            page.StorageDeletePending = false;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Public HTML binary deletion failed for page {PageId}; cleanup remains pending.", id);
        }
        return NoContent();
    }

    [AllowAnonymous]
    [HttpGet("/html/{slug}")]
    public async Task<IActionResult> PublicPage(string slug, CancellationToken cancellationToken)
    {
        if (!PublicHtmlSlug.TryNormalize(slug, out var normalized)) return NotFound();
        var page = await db.PublicHtmlPages.SingleOrDefaultAsync(
            item => item.Slug == normalized && item.IsPublished && !item.IsDeleted, cancellationToken);
        if (page is null) return NotFound();

        var now = DateTime.UtcNow;
        if (page.LastViewedAtUtc is null || page.LastViewedAtUtc < now.AddMinutes(-1))
        {
            page.LastViewedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
        }
        var title = System.Net.WebUtility.HtmlEncode(page.Title);
        var contentUrl = $"/api/public-html/{Uri.EscapeDataString(page.Slug)}/content";
        var html = $$"""
            <!doctype html>
            <html lang="en"><head>
              <meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{title}</title><meta name="referrer" content="no-referrer">
              <style>html,body{{margin:0;width:100%;height:100%;overflow:hidden}} iframe{{display:block;width:100%;height:100%;border:0}}</style>
            </head><body>
              <iframe title="{title}" src="{contentUrl}" sandbox="allow-scripts allow-forms allow-popups allow-modals" referrerpolicy="no-referrer"></iframe>
            </body></html>
            """;
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-src 'self'; base-uri 'none'; form-action 'none'";
        return Content(html, "text/html; charset=utf-8", Encoding.UTF8);
    }

    [AllowAnonymous]
    [HttpGet("{slug}/content")]
    public async Task<IActionResult> PublicContent(string slug, CancellationToken cancellationToken)
    {
        if (!PublicHtmlSlug.TryNormalize(slug, out var normalized)) return NotFound();
        var page = await db.PublicHtmlPages.AsNoTracking().SingleOrDefaultAsync(
            item => item.Slug == normalized && item.IsPublished && !item.IsDeleted, cancellationToken);
        if (page is null) return NotFound();
        try
        {
            var stream = await storage.OpenReadAsync(page.StorageKey, cancellationToken);
            Response.Headers["Cache-Control"] = "no-store";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Content-Security-Policy"] = "sandbox allow-scripts allow-forms allow-popups allow-modals; default-src https: http: data: blob:; base-uri 'none'";
            return File(stream, "text/html; charset=utf-8", enableRangeProcessing: false);
        }
        catch (FileNotFoundException) { return NotFound(); }
    }

    private static PublicHtmlPageDetails ToDetails(PublicHtmlPage page) => new(
        page.Id, page.Title, page.Slug, page.SizeBytes, page.Sha256, page.IsPublished,
        page.PublishedAtUtc, page.LastViewedAtUtc, page.CreatedUtc, page.UpdatedUtc, page.StorageDeletePending);

    public sealed record UploadPublicHtmlRequest(IFormFile? File, string? Slug, string? Title);
    public sealed record RenamePublicHtmlSlugRequest([Required, StringLength(80)] string Slug);
    public sealed record SetPublicHtmlPublishedRequest(bool IsPublished);
    public sealed record PublicHtmlListItem(Guid Id, string Title, string Slug, long SizeBytes, bool IsPublished,
        DateTime? PublishedAtUtc, DateTime? LastViewedAtUtc, DateTime CreatedUtc, DateTime UpdatedUtc);
    public sealed record PublicHtmlListResponse(IReadOnlyList<PublicHtmlListItem> Items, long Total,
        long Published, long Unpublished, int Page, int PageSize);
    public sealed record PublicHtmlPageDetails(Guid Id, string Title, string Slug, long SizeBytes, string Sha256,
        bool IsPublished, DateTime? PublishedAtUtc, DateTime? LastViewedAtUtc, DateTime CreatedUtc,
        DateTime UpdatedUtc, bool StorageDeletePending);
}

public static class PublicHtmlSlug
{
    public static bool TryNormalize(string? value, out string slug)
    {
        slug = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (slug.Length is < 1 or > 80 || slug[0] == '-' || slug[^1] == '-' || slug.Contains("--", StringComparison.Ordinal))
            return false;
        return slug.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }

    public static string FromFileName(string? fileName)
    {
        var source = Path.GetFileNameWithoutExtension(fileName ?? string.Empty).ToLowerInvariant();
        var builder = new StringBuilder(source.Length);
        var previousHyphen = false;
        foreach (var character in source)
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                previousHyphen = false;
            }
            else if (!previousHyphen && builder.Length > 0)
            {
                builder.Append('-');
                previousHyphen = true;
            }
        }
        return builder.ToString().Trim('-');
    }
}
