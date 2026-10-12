using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using ContentManagement.Server.Auth;
using ContentManagement.Server.Compression;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/json")]
public sealed class JsonDocumentsController(
    ContentManagementDbContext db,
    IContentCompressor compressor) : ControllerBase
{
    private const int MaxPageSize = 100;

    [HttpGet]
    [Authorize(Policy = ScopePolicies.JsonRead)]
    public async Task<ActionResult<JsonDocumentListResponse>> List(
        [FromQuery] string? search, [FromQuery] ContentStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var query = db.JsonDocuments.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            if (Guid.TryParse(term, out var id))
                query = query.Where(x => x.Code.Contains(term) || x.DocumentType.Contains(term) || x.Sha256 == term || x.Id == id);
            else
                query = query.Where(x => x.Code.Contains(term) || x.DocumentType.Contains(term) || x.Sha256 == term);
        }
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);

        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.UpdatedUtc)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.Code, x.DocumentType, x.UncompressedSizeBytes, x.CompressedSizeBytes,
                x.Sha256, x.CreatedUtc, x.UpdatedUtc, x.Status, x.OwnerId, x.RowVersion })
            .ToListAsync(cancellationToken);

        return Ok(new JsonDocumentListResponse(rows.Select(x => new JsonDocumentListItem(
            x.Id, x.Code, x.DocumentType, x.UncompressedSizeBytes, x.CompressedSizeBytes, x.Sha256,
            x.CreatedUtc, x.UpdatedUtc, x.Status, x.OwnerId, Convert.ToBase64String(x.RowVersion))).ToList(),
            total, page, pageSize));
    }

    [HttpPost]
    [Authorize(Policy = ScopePolicies.JsonWrite)]
    [Consumes("application/json")]
    public async Task<ActionResult<JsonDocumentDetails>> Create(
        [FromBody] CreateJsonDocumentRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DocumentType) || request.DocumentType.Trim().Length > 200)
            return BadRequest(new { message = "DocumentType is required and must not exceed 200 characters." });
        if (request.Json is null)
            return BadRequest(new { message = "Json is required." });
        var code = NormalizeOrGenerateCode(request.Code);
        if (code is null)
            return BadRequest(new { message = "Code may contain only letters, numbers, dots, underscores and hyphens, and must start with a letter or number." });
        if (await db.JsonDocuments.AnyAsync(x => x.Code == code, cancellationToken))
            return Conflict(new { message = "A JSON document with this code already exists." });

        CompressedContent compressed;
        try { compressed = compressor.CompressJson(request.Json); }
        catch (System.Text.Json.JsonException) { return BadRequest(new { message = "Json must contain valid JSON." }); }

        var now = DateTime.UtcNow;
        var entity = new JsonDocumentRecord
        {
            Id = Guid.NewGuid(), Code = code, DocumentType = request.DocumentType.Trim(),
            CompressedPayload = compressed.Bytes, UncompressedSizeBytes = compressed.UncompressedSizeBytes,
            CompressedSizeBytes = compressed.Bytes.LongLength, Sha256 = compressed.Sha256,
            CreatedUtc = now, UpdatedUtc = now, Status = ContentStatus.Active,
            OwnerId = User.FindFirst("sub")?.Value
        };
        db.JsonDocuments.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (await db.JsonDocuments.AnyAsync(x => x.Code == code, cancellationToken))
                return Conflict(new { message = "A JSON document with this code already exists." });
            throw;
        }
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, ToDetails(entity, request.Json));
    }

    [HttpPut("by-code/{code}")]
    [Authorize(Policy = ScopePolicies.JsonWrite)]
    [Consumes("application/json")]
    public async Task<ActionResult<JsonDocumentDetails>> UpsertByCode(
        string code, [FromBody] UpsertJsonDocumentRequest request, CancellationToken cancellationToken)
    {
        var normalizedCode = NormalizeOrGenerateCode(code);
        if (normalizedCode is null || !string.Equals(code, code.Trim(), StringComparison.Ordinal))
            return BadRequest(new { message = "Code must be 1-100 characters, start with a letter or number, and contain only letters, numbers, dots, underscores or hyphens." });
        if (string.IsNullOrWhiteSpace(request.DocumentType) || request.DocumentType.Trim().Length > 200)
            return BadRequest(new { message = "DocumentType is required and must not exceed 200 characters." });
        if (request.Json is null)
            return BadRequest(new { message = "Json is required." });

        CompressedContent compressed;
        try { compressed = compressor.CompressJson(request.Json); }
        catch (System.Text.Json.JsonException) { return BadRequest(new { message = "Json must contain valid JSON." }); }

        // The unique Code index plus SERIALIZABLE range locking makes lookup-and-create
        // atomic for concurrent requests using the same code.
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);

        var entity = await db.JsonDocuments
            .SingleOrDefaultAsync(x => x.Code == normalizedCode, cancellationToken);
        var now = DateTime.UtcNow;
        var created = entity is null;

        if (entity is null)
        {
            entity = new JsonDocumentRecord
            {
                Id = Guid.NewGuid(),
                Code = normalizedCode,
                CreatedUtc = now,
                Status = ContentStatus.Active
            };
            db.JsonDocuments.Add(entity);
        }
        else if (entity.Status != ContentStatus.Active)
        {
            return Conflict(new { message = "A document with this code exists but is not active; it cannot be upserted." });
        }

        entity.DocumentType = request.DocumentType.Trim();
        entity.CompressedPayload = compressed.Bytes;
        entity.UncompressedSizeBytes = compressed.UncompressedSizeBytes;
        entity.CompressedSizeBytes = compressed.Bytes.LongLength;
        entity.Sha256 = compressed.Sha256;
        entity.UpdatedUtc = now;
        entity.OwnerId ??= User.FindFirst("sub")?.Value;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        var details = ToDetails(entity, request.Json);
        if (created)
            return CreatedAtAction(nameof(GetByCode), new { code = normalizedCode }, details);
        return Ok(details);
    }

    [HttpGet("by-code/{code}")]
    [Authorize(Policy = ScopePolicies.JsonRead)]
    public async Task<ActionResult<JsonDocumentDetails>> GetByCode(string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 100)
            return BadRequest(new { message = "Code is required and must not exceed 100 characters." });

        var normalizedCode = code.Trim();
        var entity = await db.JsonDocuments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Code == normalizedCode, cancellationToken);
        if (entity is null || entity.Status is ContentStatus.MarkedForDeletion or ContentStatus.Deleting or ContentStatus.Deleted)
            return NotFound();

        try { return Ok(ToDetails(entity, compressor.DecompressJson(entity.CompressedPayload))); }
        catch (System.Text.Json.JsonException)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Stored JSON is corrupted.");
        }
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = ScopePolicies.JsonRead)]
    public async Task<ActionResult<JsonDocumentDetails>> Get(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.JsonDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null || entity.Status is ContentStatus.MarkedForDeletion or ContentStatus.Deleting or ContentStatus.Deleted)
            return NotFound();
        try { return Ok(ToDetails(entity, compressor.DecompressJson(entity.CompressedPayload))); }
        catch (System.Text.Json.JsonException)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Stored JSON is corrupted.");
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = ScopePolicies.JsonWrite)]
    [Consumes("application/json")]
    public async Task<ActionResult<JsonDocumentDetails>> Update(
        Guid id, [FromBody] UpdateJsonDocumentRequest request, CancellationToken cancellationToken)
    {
        var entity = await db.JsonDocuments.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null || entity.Status != ContentStatus.Active) return NotFound();
        if (string.IsNullOrWhiteSpace(request.DocumentType) || request.DocumentType.Trim().Length > 200)
            return BadRequest(new { message = "DocumentType is required and must not exceed 200 characters." });
        if (request.Json is null) return BadRequest(new { message = "Json is required." });
        var code = string.IsNullOrWhiteSpace(request.Code) ? entity.Code : NormalizeOrGenerateCode(request.Code);
        if (code is null)
            return BadRequest(new { message = "Code may contain only letters, numbers, dots, underscores and hyphens, and must start with a letter or number." });
        if (await db.JsonDocuments.AnyAsync(x => x.Id != id && x.Code == code, cancellationToken))
            return Conflict(new { message = "A JSON document with this code already exists." });

        CompressedContent compressed;
        try { compressed = compressor.CompressJson(request.Json); }
        catch (System.Text.Json.JsonException) { return BadRequest(new { message = "Json must contain valid JSON." }); }

        entity.Code = code;
        entity.DocumentType = request.DocumentType.Trim();
        entity.CompressedPayload = compressed.Bytes;
        entity.UncompressedSizeBytes = compressed.UncompressedSizeBytes;
        entity.CompressedSizeBytes = compressed.Bytes.LongLength;
        entity.Sha256 = compressed.Sha256;
        entity.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(ToDetails(entity, request.Json));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = ScopePolicies.JsonDelete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.JsonDocuments.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();

        db.JsonDocuments.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static string? NormalizeOrGenerateCode(string? requestedCode)
    {
        if (string.IsNullOrWhiteSpace(requestedCode))
            return $"json-{Guid.NewGuid():N}";
        var code = requestedCode.Trim();
        return code.Length <= 100 && Regex.IsMatch(code, @"^[A-Za-z0-9][A-Za-z0-9._-]*$")
            ? code
            : null;
    }

    private static JsonDocumentDetails ToDetails(JsonDocumentRecord x, string json) =>
        new(x.Id, x.Code, x.DocumentType, json, x.UncompressedSizeBytes, x.CompressedSizeBytes,
            x.Sha256, x.CreatedUtc, x.UpdatedUtc, x.Status, x.OwnerId, Convert.ToBase64String(x.RowVersion));
}

public sealed record CreateJsonDocumentRequest(
    [StringLength(100)] string? Code,
    [Required, StringLength(200, MinimumLength = 1)] string DocumentType,
    [Required] string Json);
public sealed record UpdateJsonDocumentRequest(
    [StringLength(100)] string? Code,
    [Required, StringLength(200, MinimumLength = 1)] string DocumentType,
    [Required] string Json);
public sealed record UpsertJsonDocumentRequest(
    [Required, StringLength(200, MinimumLength = 1)] string DocumentType,
    [Required] string Json);
public sealed record JsonDocumentListItem(
    Guid Id, string Code, string DocumentType, long UncompressedSizeBytes, long CompressedSizeBytes,
    string Sha256, DateTime CreatedUtc, DateTime UpdatedUtc, ContentStatus Status,
    string? OwnerId, string RowVersion);
public sealed record JsonDocumentListResponse(
    IReadOnlyList<JsonDocumentListItem> Items, long Total, int Page, int PageSize);
public sealed record JsonDocumentDetails(
    Guid Id, string Code, string DocumentType, string Json, long UncompressedSizeBytes, long CompressedSizeBytes,
    string Sha256, DateTime CreatedUtc, DateTime UpdatedUtc, ContentStatus Status,
    string? OwnerId, string RowVersion);
