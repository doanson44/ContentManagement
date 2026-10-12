using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/admin/api-clients")]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Roles = "Administrator")]
public sealed class ApiClientsController(ContentManagementDbContext db) : ControllerBase
{
    private static readonly string[] SupportedScopes =
    [
        "files.read", "files.write", "files.delete",
        "json.read", "json.write", "json.delete"
    ];

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApiClientResponse>>> List(CancellationToken cancellationToken)
    {
        var clients = await db.ApiClients.AsNoTracking()
            .OrderBy(client => client.Name)
            .ToListAsync(cancellationToken);

        return Ok(clients.Select(ToResponse).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiClientResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var client = await db.ApiClients.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return client is null ? NotFound() : Ok(ToResponse(client));
    }

    [HttpPost]
    public async Task<ActionResult<ApiClientCreatedResponse>> Create(
        [FromBody] CreateApiClientRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryNormalize(request.Name, request.Scopes, out var name, out var scopes, out var error))
            return BadRequest(new { message = error });

        var now = DateTime.UtcNow;
        var clientId = Guid.NewGuid().ToString("N");
        var secret = CreateSecret();
        var entity = new ApiClient
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = name,
            SecretHash = HashSecret(secret),
            ScopesJson = JsonSerializer.Serialize(scopes),
            IsActive = true,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        db.ApiClients.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = entity.Id },
            new ApiClientCreatedResponse(ToResponse(entity), FormatApiKey(clientId, secret)));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiClientResponse>> Update(
        Guid id,
        [FromBody] UpdateApiClientRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryNormalize(request.Name, request.Scopes, out var name, out var scopes, out var error))
            return BadRequest(new { message = error });

        var entity = await db.ApiClients.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();

        if (!string.IsNullOrWhiteSpace(request.RowVersion))
        {
            byte[] suppliedVersion;
            try { suppliedVersion = Convert.FromBase64String(request.RowVersion); }
            catch (FormatException) { return BadRequest(new { message = "RowVersion must be a valid Base64 value." }); }

            if (!CryptographicOperations.FixedTimeEquals(suppliedVersion, entity.RowVersion))
                return Conflict(new { message = "API client changed. Refresh and retry." });
        }

        entity.Name = name;
        entity.ScopesJson = JsonSerializer.Serialize(scopes);
        entity.IsActive = request.IsActive;
        entity.UpdatedUtc = DateTime.UtcNow;

        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "API client changed. Refresh and retry." });
        }

        return Ok(ToResponse(entity));
    }

    [HttpPost("{id:guid}/rotate-secret")]
    public async Task<ActionResult<ApiClientCreatedResponse>> RotateSecret(
        Guid id,
        CancellationToken cancellationToken)
    {
        var entity = await db.ApiClients.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();

        var secret = CreateSecret();
        entity.SecretHash = HashSecret(secret);
        entity.UpdatedUtc = DateTime.UtcNow;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "API client changed. Refresh and retry." });
        }

        return Ok(new ApiClientCreatedResponse(ToResponse(entity), FormatApiKey(entity.ClientId, secret)));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.ApiClients.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null)
            return NoContent();

        db.ApiClients.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static bool TryNormalize(
        string? requestedName,
        string[]? requestedScopes,
        out string name,
        out string[] scopes,
        out string error)
    {
        name = requestedName?.Trim() ?? string.Empty;
        scopes = [];
        error = string.Empty;

        if (name.Length is < 1 or > 100 || name.Any(char.IsControl))
        {
            error = "Name is required, must be at most 100 characters, and cannot contain control characters.";
            return false;
        }

        if (requestedScopes is null || requestedScopes.Length == 0)
        {
            error = "Select at least one permission scope.";
            return false;
        }

        scopes = requestedScopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (scopes.Length == 0 || scopes.Any(scope => !SupportedScopes.Contains(scope, StringComparer.Ordinal)))
        {
            error = "One or more permission scopes are unsupported.";
            return false;
        }

        return true;
    }

    private static string CreateSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string FormatApiKey(string clientId, string secret) => $"cm_{clientId}.{secret}";

    private static ApiClientResponse ToResponse(ApiClient entity) =>
        new(entity.Id, entity.ClientId, entity.Name,
            JsonSerializer.Deserialize<string[]>(entity.ScopesJson) ?? [],
            entity.IsActive, entity.CreatedUtc, entity.UpdatedUtc,
            Convert.ToBase64String(entity.RowVersion));

    public sealed record CreateApiClientRequest(
        [Required, StringLength(100, MinimumLength = 1)] string Name,
        [Required, MinLength(1)] string[] Scopes);

    public sealed record UpdateApiClientRequest(
        [Required, StringLength(100, MinimumLength = 1)] string Name,
        [Required, MinLength(1)] string[] Scopes,
        bool IsActive,
        string? RowVersion);

    public sealed record ApiClientResponse(
        Guid Id, string ClientId, string Name, string[] Scopes, bool IsActive,
        DateTime CreatedUtc, DateTime UpdatedUtc, string RowVersion);

    public sealed record ApiClientCreatedResponse(ApiClientResponse Client, string ApiKey);
}
