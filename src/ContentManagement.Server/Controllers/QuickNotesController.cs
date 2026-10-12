using System.ComponentModel.DataAnnotations;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/admin/quick-notes")]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Roles = "Administrator")]
public sealed class QuickNotesController(ContentManagementDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<QuickNoteResponse>>> List(CancellationToken cancellationToken)
    {
        var notes = await db.QuickNotes.AsNoTracking()
            .OrderByDescending(note => note.IsPinned)
            .ThenByDescending(note => note.UpdatedUtc)
            .Select(note => new QuickNoteResponse(
                note.Id, note.Title, note.Content, note.IsPinned,
                note.CreatedUtc, note.UpdatedUtc, Convert.ToBase64String(note.RowVersion)))
            .ToListAsync(cancellationToken);

        return Ok(notes);
    }

    [HttpPost]
    public async Task<ActionResult<QuickNoteResponse>> Create(
        [FromBody] SaveQuickNoteRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryNormalize(request.Title, request.Content, out var title, out var content, out var error))
            return BadRequest(new { message = error });

        var now = DateTime.UtcNow;
        var note = new QuickNote
        {
            Id = Guid.NewGuid(),
            Title = title,
            Content = content,
            IsPinned = false,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        db.QuickNotes.Add(note);
        await db.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = note.Id }, ToResponse(note));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<QuickNoteResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var note = await db.QuickNotes.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return note is null ? NotFound() : Ok(ToResponse(note));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<QuickNoteResponse>> Update(
        Guid id,
        [FromBody] SaveQuickNoteRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryNormalize(request.Title, request.Content, out var title, out var content, out var error))
            return BadRequest(new { message = error });

        if (string.IsNullOrWhiteSpace(request.RowVersion))
            return BadRequest(new { message = "A row version is required to update a note." });

        byte[] rowVersion;
        try { rowVersion = Convert.FromBase64String(request.RowVersion); }
        catch (FormatException) { return BadRequest(new { message = "Invalid row version." }); }

        var note = await db.QuickNotes.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (note is null) return NotFound();

        db.Entry(note).Property(item => item.RowVersion).OriginalValue = rowVersion;
        note.Title = title;
        note.Content = content;
        note.IsPinned = request.IsPinned;
        note.UpdatedUtc = DateTime.UtcNow;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "This note changed elsewhere. Reload it before saving again." });
        }

        return Ok(ToResponse(note));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var note = await db.QuickNotes.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (note is null) return NotFound();

        db.QuickNotes.Remove(note);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static bool TryNormalize(
        string? rawTitle, string? rawContent,
        out string title, out string content, out string error)
    {
        title = (rawTitle ?? string.Empty).Trim();
        content = rawContent ?? string.Empty;
        error = string.Empty;

        if (title.Length == 0 || title.Length > 200)
        {
            error = "Title is required and must not exceed 200 characters.";
            return false;
        }

        if (content.Length > 20000)
        {
            error = "Content must not exceed 20,000 characters.";
            return false;
        }

        return true;
    }

    private static QuickNoteResponse ToResponse(QuickNote note) =>
        new(note.Id, note.Title, note.Content, note.IsPinned, note.CreatedUtc, note.UpdatedUtc,
            Convert.ToBase64String(note.RowVersion));

    public sealed record SaveQuickNoteRequest(
        [Required, StringLength(200, MinimumLength = 1)] string Title,
        [StringLength(20000)] string? Content,
        bool IsPinned = false,
        string? RowVersion = null);

    public sealed record QuickNoteResponse(
        Guid Id, string Title, string Content, bool IsPinned,
        DateTime CreatedUtc, DateTime UpdatedUtc, string RowVersion);
}
