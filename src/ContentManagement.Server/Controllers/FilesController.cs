using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Security.Claims;
using ContentManagement.Server.Auth;
using ContentManagement.Server.Configuration;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using ContentManagement.Server.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/files")]
public sealed class FilesController(
    ContentManagementDbContext db,
    IFileStorage storage,
    IVideoPreviewService videoPreviewService,
    IOptions<ContentManagementOptions> options,
    ExternalFileApiClient externalFiles,
    IOptions<ExternalStorageOptions> externalStorageOptions,
    ILogger<FilesController> logger) : ControllerBase
{
    private readonly ExternalStorageOptions _externalStorage = externalStorageOptions.Value;
    private const int MaxPageSize = 100;
    private static readonly SemaphoreSlim ExternalUploadGate = new(1, 1);

    [HttpGet]
    [Authorize(Policy = ScopePolicies.FilesRead)]
    public async Task<ActionResult<FileListResponse>> List(
        [FromQuery] string? search, [FromQuery] ContentStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var query = ApplyOwnerScope(db.Files.AsNoTracking())
            .Where(f => f.Status != ContentStatus.Deleted);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(f => f.FileName.Contains(term) || f.FilePath.Contains(term) || f.Sha256 == term);
        }
        if (status.HasValue) query = query.Where(f => f.Status == status.Value);
        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderByDescending(f => f.CreatedUtc)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(f => new
            {
                f.Id, f.FilePath, f.FileName, f.ContentType, f.SizeBytes, f.Sha256,
                f.Status, f.UploadSource, f.OwnerId, f.Type, f.CreatedUtc, f.UpdatedUtc, f.RowVersion
            })
            .ToListAsync(cancellationToken);
        var items = rows.Select(f => new FileListItem(f.Id, ToLogicalFilePath(f.FilePath), f.FileName, f.ContentType,
            f.SizeBytes, f.Sha256, f.Status, f.UploadSource, f.OwnerId, f.Type, f.CreatedUtc, f.UpdatedUtc,
            Convert.ToBase64String(f.RowVersion),
            $"/api/files/by-path/content?path={Uri.EscapeDataString(ToLogicalFilePath(f.FilePath))}")).ToList();
        return Ok(new FileListResponse(items, total, page, pageSize));
    }

    [HttpPost]
    [Authorize(Policy = ScopePolicies.FilesWrite)]
    public async Task<ActionResult<FileDetails>> Upload(
        [FromForm] IFormFile? file,
        [FromForm] string? filePath,
        [FromForm] bool @override = false,
        CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length <= 0)
            return BadRequest(new { message = "Choose a non-empty file." });
        if (file.Length > options.Value.MaxUploadBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { message = "File exceeds configured upload limit." });

        var fileName = Path.GetFileName((file.FileName ?? string.Empty).Replace((char)92, '/')).Trim();
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255 || fileName.Any(char.IsControl))
            return BadRequest(new { message = "Invalid file name." });

        var now = DateTime.UtcNow;
        var generatedId = Guid.NewGuid();
        string storedFilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            storedFilePath = BuildStoredFilePath($"files/{now:yyyy/MM/dd}/{generatedId:N}");
        }
        else
        {
            if (!TryResolveFilePath(filePath, out storedFilePath))
                return BadRequest(new { message = "filePath must be a relative path with safe path segments (maximum 450 characters)." });
        }

        var existing = await ApplyOwnerScope(db.Files)
            .SingleOrDefaultAsync(f => f.FilePath == storedFilePath, cancellationToken);
        if (existing is not null)
        {
            if (!@override)
                return Conflict(new { message = "filePath is already assigned to a file and cannot be reused." });
            if (existing.Status != ContentStatus.Active)
                return Conflict(new { message = "A file at this path is pending deletion and cannot be overwritten." });
        }
        else if (await db.Files.AsNoTracking().AnyAsync(f => f.FilePath == storedFilePath, cancellationToken))
        {
            return Conflict(new { message = "filePath is already assigned to a file and cannot be reused." });
        }

        var settings = await db.SystemSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings?.ExternalStoragePreferred == true && !_externalStorage.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "External storage is enabled but ExternalStorage:BaseUrl or ExternalStorage:ApiKey is missing from server configuration." });

        var oldStorageKey = existing?.StorageKey;
        var oldStorageSource = existing?.UploadSource;
        if (existing?.UploadSource == FileUploadSource.ExternalServer && !_externalStorage.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "The existing file uses external storage, which is not configured. The file was not changed." });

        if (settings?.ExternalStoragePreferred == true && _externalStorage.IsConfigured)
        {
            await ExternalUploadGate.WaitAsync(cancellationToken);
            try
            {
                settings = await db.SystemSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
                var usedBytes = await db.Files.AsNoTracking()
                    .Where(f => f.UploadSource == FileUploadSource.ExternalServer && f.Status != ContentStatus.Deleted)
                    .Select(f => (long?)f.SizeBytes).SumAsync(cancellationToken) ?? 0L;
                var reclaimableBytes = existing?.UploadSource == FileUploadSource.ExternalServer ? existing.SizeBytes : 0L;
                var remaining = Math.Max(0, (settings?.ExternalStorageQuotaBytes ?? 0) - usedBytes + reclaimableBytes);
                if (settings is not null && file.Length <= remaining)
                {
                    var externalKey = $"{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}.bin";
                    StoredBinary externalBinary;
                    await using (var input = file.OpenReadStream())
                    {
                        try
                        {
                            externalBinary = await externalFiles.UploadAsync(externalKey, fileName, input, cancellationToken);
                        }
                        catch (HttpRequestException exception)
                        {
                            logger.LogError(exception, "External File API upload failed; upload was not redirected to local storage.");
                            return StatusCode(StatusCodes.Status502BadGateway,
                                new { message = "External File API upload failed. No local fallback was performed." });
                        }
                    }

                    if (externalBinary.SizeBytes > remaining || externalBinary.SizeBytes != file.Length)
                    {
                        await TryDeleteNewBinaryAsync(FileUploadSource.ExternalServer, externalKey);
                        logger.LogError("External File API returned an unexpected file size for upload.");
                        return StatusCode(StatusCodes.Status502BadGateway,
                            new { message = "External File API returned unexpected file size metadata." });
                    }

                    var entity = existing ?? CreateEntity(file, externalBinary, FileUploadSource.ExternalServer, storedFilePath, generatedId);
                    ApplyUploadedBinary(entity, file, externalBinary, FileUploadSource.ExternalServer, storedFilePath);
                    try
                    {
                        if (existing is null) db.Files.Add(entity);
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        await TryDeleteNewBinaryAsync(FileUploadSource.ExternalServer, externalKey);
                        return Conflict(new { message = "File metadata changed. Refresh and retry." });
                    }
                    catch (Exception exception)
                    {
                        await TryDeleteNewBinaryAsync(FileUploadSource.ExternalServer, externalKey);
                        if (exception.GetBaseException() is SqlException { Number: 2601 or 2627 })
                            return Conflict(new { message = "filePath is already assigned to a file and cannot be reused." });
                        throw;
                    }

                    if (oldStorageKey is not null)
                        await TryDeleteNewBinaryAsync(oldStorageSource!.Value, oldStorageKey);
                    return existing is null
                        ? CreatedAtAction(nameof(Get), new { id = entity.Id }, ToDetails(entity))
                        : Ok(ToDetails(entity));
                }
            }
            finally
            {
                ExternalUploadGate.Release();
            }
        }

        StoredBinary binary;
        await using (var input = file.OpenReadStream())
        {
            try { binary = await storage.SaveAsync(input, options.Value.MaxUploadBytes, cancellationToken); }
            catch (InvalidDataException) { return StatusCode(StatusCodes.Status413PayloadTooLarge); }
        }

        var localEntity = existing ?? CreateEntity(file, binary, FileUploadSource.Local, storedFilePath, generatedId);
        ApplyUploadedBinary(localEntity, file, binary, FileUploadSource.Local, storedFilePath);
        try
        {
            if (existing is null) db.Files.Add(localEntity);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await TryDeleteNewBinaryAsync(FileUploadSource.Local, binary.StorageKey);
            return Conflict(new { message = "File metadata changed. Refresh and retry." });
        }
        catch (Exception exception)
        {
            await TryDeleteNewBinaryAsync(FileUploadSource.Local, binary.StorageKey);
            if (exception.GetBaseException() is SqlException { Number: 2601 or 2627 })
                return Conflict(new { message = "filePath is already assigned to a file and cannot be reused." });
            throw;
        }

        if (oldStorageKey is not null)
            await TryDeleteNewBinaryAsync(oldStorageSource!.Value, oldStorageKey);
        return existing is null
            ? CreatedAtAction(nameof(Get), new { id = localEntity.Id }, ToDetails(localEntity))
            : Ok(ToDetails(localEntity));
    }

    private StoredFile CreateEntity(
        IFormFile file, StoredBinary binary, FileUploadSource source, string storedFilePath, Guid id)
    {
        var now = DateTime.UtcNow;
        var (ownerType, ownerId) = ResolveOwner();
        var entity = new StoredFile
        {
            Id = id,
            FilePath = storedFilePath,
            CreatedUtc = now,
            Status = ContentStatus.Active,
            Type = ownerType,
            OwnerId = ownerId
        };
        ApplyUploadedBinary(entity, file, binary, source, storedFilePath);
        return entity;
    }

    private void ApplyUploadedBinary(
        StoredFile entity, IFormFile file, StoredBinary binary, FileUploadSource source, string storedFilePath)
    {
        entity.FilePath = storedFilePath;
        entity.FileName = Path.GetFileName((file.FileName ?? string.Empty).Replace((char)92, '/')).Trim();
        entity.ContentType = ResolveContentType(file.ContentType, file.FileName);
        entity.StorageKey = binary.StorageKey;
        entity.SizeBytes = binary.SizeBytes;
        entity.Sha256 = binary.Sha256;
        entity.UpdatedUtc = DateTime.UtcNow;
        entity.Status = ContentStatus.Active;
        entity.UploadSource = source;
    }

    private (FileOwnerType Type, string? OwnerId) ResolveOwner()
    {
        var clientId = User.FindFirst("client_id")?.Value;
        if (!string.IsNullOrWhiteSpace(clientId))
            return (FileOwnerType.Client, clientId);

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub")?.Value;
        return string.IsNullOrWhiteSpace(userId)
            ? (FileOwnerType.Legacy, null)
            : (FileOwnerType.User, userId);
    }

    private IQueryable<StoredFile> ApplyOwnerScope(IQueryable<StoredFile> query)
    {
        var clientId = User.FindFirst("client_id")?.Value;
        if (!string.IsNullOrWhiteSpace(clientId))
            return query.Where(file => file.Type == FileOwnerType.Client && file.OwnerId == clientId);

        if (User.IsInRole("Administrator"))
            return query;

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub")?.Value;
        return string.IsNullOrWhiteSpace(userId)
            ? query.Where(_ => false)
            : query.Where(file => file.Type == FileOwnerType.User && file.OwnerId == userId);
    }

    private bool TryResolveFilePath(string? requestedPath, out string storedFilePath)
    {
        storedFilePath = string.Empty;
        if (!FilePathNormalizer.TryNormalize(requestedPath, out var normalizedPath))
            return false;

        var clientId = User.FindFirst("client_id")?.Value;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            storedFilePath = normalizedPath;
            return true;
        }

        var prefix = $"clients/{clientId}/";
        if (normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        storedFilePath = prefix + normalizedPath;
        return storedFilePath.Length <= 450;
    }

    private string BuildStoredFilePath(string relativePath)
    {
        var clientId = User.FindFirst("client_id")?.Value;
        var storedPath = string.IsNullOrWhiteSpace(clientId) ? relativePath : $"clients/{clientId}/{relativePath}";
        if (storedPath.Length > 450)
            throw new InvalidOperationException("Generated file path exceeds the configured maximum length.");
        return storedPath;
    }

    private string ToLogicalFilePath(string storedFilePath)
    {
        var clientId = User.FindFirst("client_id")?.Value;
        var prefix = string.IsNullOrWhiteSpace(clientId) ? null : $"clients/{clientId}/";
        return prefix is not null && storedFilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? storedFilePath[prefix.Length..]
            : storedFilePath;
    }

    private async Task TryDeleteNewBinaryAsync(FileUploadSource source, string storageKey)
    {
        try
        {
            if (source == FileUploadSource.ExternalServer)
                await externalFiles.DeleteAsync(storageKey, CancellationToken.None);
            else
                await storage.DeleteAsync(storageKey, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to clean up file binary {StorageKey}.", storageKey);
        }
    }

    [HttpGet("by-path")]
    [Authorize(Policy = ScopePolicies.FilesRead)]
    public async Task<ActionResult<FileDetails>> GetByPath(
        [FromQuery, Required] string? path, CancellationToken cancellationToken)
    {
        if (!TryResolveFilePath(path, out var normalizedPath))
            return BadRequest(new { message = "path must be a relative path with safe path segments (maximum 450 characters)." });

        var file = await ApplyOwnerScope(db.Files.AsNoTracking()).SingleOrDefaultAsync(
            f => f.FilePath == normalizedPath && f.Status == ContentStatus.Active, cancellationToken);
        return file is null ? NotFound() : Ok(ToDetails(file));
    }

    [HttpGet("by-path/content")]
    [Authorize(Policy = ScopePolicies.FilesRead)]
    public async Task<IActionResult> ContentByPath(
        [FromQuery, Required] string? path, CancellationToken cancellationToken)
    {
        if (!TryResolveFilePath(path, out var normalizedPath))
            return BadRequest(new { message = "path must be a relative path with safe path segments (maximum 450 characters)." });

        var file = await ApplyOwnerScope(db.Files.AsNoTracking()).SingleOrDefaultAsync(
            f => f.FilePath == normalizedPath && f.Status == ContentStatus.Active, cancellationToken);
        return file is null ? NotFound() : await Content(file.Id, cancellationToken);
    }

    [HttpGet("by-path/download")]
    [Authorize(Policy = ScopePolicies.FilesRead)]
    public async Task<IActionResult> DownloadByPath(
        [FromQuery, Required] string? path, CancellationToken cancellationToken)
    {
        if (!TryResolveFilePath(path, out var normalizedPath))
            return BadRequest(new { message = "path must be a relative path with safe path segments (maximum 450 characters)." });

        var file = await ApplyOwnerScope(db.Files.AsNoTracking()).SingleOrDefaultAsync(
            f => f.FilePath == normalizedPath && f.Status == ContentStatus.Active, cancellationToken);
        return file is null ? NotFound() : await Download(file.Id, cancellationToken);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = ScopePolicies.FilesRead)]
    public async Task<ActionResult<FileDetails>> Get(Guid id, CancellationToken cancellationToken)
    {
        var file = await ApplyOwnerScope(db.Files.AsNoTracking()).SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (file is null || file.Status is ContentStatus.MarkedForDeletion or ContentStatus.Deleting or ContentStatus.Deleted)
            return NotFound();
        return Ok(ToDetails(file));
    }

    [HttpGet("{id:guid}/content")]
    [Authorize(Policy = ScopePolicies.FilesRead)]
    public async Task<IActionResult> Content(Guid id, CancellationToken cancellationToken)
    {
        var file = await ApplyOwnerScope(db.Files.AsNoTracking()).SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (file is null || file.Status != ContentStatus.Active) return NotFound();
        try
        {
            if (file.UploadSource == FileUploadSource.ExternalServer)
            {
                var externalResponse = await externalFiles.GetFileAsync(file.StorageKey, cancellationToken);
                Response.RegisterForDispose(externalResponse);
                Response.Headers["Content-Disposition"] = $"inline; filename*=UTF-8''{Uri.EscapeDataString(file.FileName)}";
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                return File(await externalResponse.Content.ReadAsStreamAsync(cancellationToken),
                    ResolveContentType(file.ContentType, file.FileName));
            }
            var stream = await storage.OpenReadAsync(file.StorageKey, cancellationToken);
            Response.Headers["Content-Disposition"] = $"inline; filename*=UTF-8''{Uri.EscapeDataString(file.FileName)}";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(stream, ResolveContentType(file.ContentType, file.FileName), enableRangeProcessing: true);
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (HttpRequestException exception)
        {
            logger.LogError(exception, "External File API content read failed for file {FileId}.", id);
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "External file storage is unavailable." });
        }
    }

    [HttpGet("{id:guid}/preview")]
    [Authorize(Policy = ScopePolicies.FilesRead)]
    public async Task<IActionResult> Preview(Guid id, CancellationToken cancellationToken)
    {
        var file = await ApplyOwnerScope(db.Files.AsNoTracking()).SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (file is null || file.Status != ContentStatus.Active) return NotFound();

        if (!Path.GetExtension(file.FileName).Equals(".wmv", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Transcoded preview is only available for WMV files." });
        if (file.UploadSource == FileUploadSource.ExternalServer)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "WMV transcoded preview is not yet supported for files stored in the External File API. Download or view the original file instead." });

        try
        {
            var previewPath = await videoPreviewService.GetWmvPreviewPathAsync(file.StorageKey, file.Sha256, cancellationToken);
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return PhysicalFile(previewPath, "video/mp4", enableRangeProcessing: true);
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (TimeoutException)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { message = "WMV preview conversion exceeded the time limit." });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(exception, "WMV preview unavailable for file {FileId}.", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "WMV preview is unavailable. Check FFmpeg installation and server configuration." });
        }
    }

    [HttpGet("{id:guid}/download")]
    [Authorize(Policy = ScopePolicies.FilesRead)]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var file = await ApplyOwnerScope(db.Files.AsNoTracking()).SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (file is null || file.Status != ContentStatus.Active) return NotFound();
        try
        {
            if (file.UploadSource == FileUploadSource.ExternalServer)
            {
                var externalResponse = await externalFiles.GetFileAsync(file.StorageKey, cancellationToken);
                Response.RegisterForDispose(externalResponse);
                return File(await externalResponse.Content.ReadAsStreamAsync(cancellationToken),
                    ResolveContentType(file.ContentType, file.FileName), file.FileName);
            }
            return File(await storage.OpenReadAsync(file.StorageKey, cancellationToken),
                ResolveContentType(file.ContentType, file.FileName), file.FileName, enableRangeProcessing: true);
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (HttpRequestException exception)
        {
            logger.LogError(exception, "External File API download failed for file {FileId}.", id);
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "External file storage is unavailable." });
        }
    }

    [HttpPatch("{id:guid}")]
    [Authorize(Policy = ScopePolicies.FilesWrite)]
    public async Task<IActionResult> Rename(Guid id, RenameFileRequest request, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(request.FileName?.Trim());
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Any(char.IsControl))
            return BadRequest(new { message = "File name must be 1–255 characters and contain no control characters." });
        var file = await ApplyOwnerScope(db.Files).SingleOrDefaultAsync(f => f.Id == id && f.Status == ContentStatus.Active, cancellationToken);
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

    [HttpPost("{id:guid}/restore")]
    [Authorize(Policy = ScopePolicies.FilesWrite)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
    {
        var file = await ApplyOwnerScope(db.Files).SingleOrDefaultAsync(
            f => f.Id == id && f.Status == ContentStatus.MarkedForDeletion, cancellationToken);
        if (file is null)
            return Conflict(new { message = "Only files marked for deletion can be restored." });

        file.Status = ContentStatus.Active;
        file.UpdatedUtc = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "File metadata changed. Refresh and retry." });
        }

        logger.LogInformation("Restored file {FileId} from marked-for-deletion state.", file.Id);
        return Ok(ToDetails(file));
    }

    [HttpDelete("by-path")]
    [Authorize(Policy = ScopePolicies.FilesDelete)]
    public async Task<IActionResult> DeleteByPath(
        [FromQuery, Required] string? path, CancellationToken cancellationToken)
    {
        if (!TryResolveFilePath(path, out var normalizedPath))
            return BadRequest(new { message = "path must be a relative path with safe path segments (maximum 450 characters)." });

        var file = await ApplyOwnerScope(db.Files).SingleOrDefaultAsync(
            f => f.FilePath == normalizedPath, cancellationToken);
        if (file is null) return NoContent();
        return await MarkForDeletionAsync(file, cancellationToken);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = ScopePolicies.FilesDelete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var file = await ApplyOwnerScope(db.Files).SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (file is null) return NoContent();
        return await MarkForDeletionAsync(file, cancellationToken);
    }

    private async Task<IActionResult> MarkForDeletionAsync(StoredFile file, CancellationToken cancellationToken)
    {
        if (file.Status == ContentStatus.Active)
        {
            file.Status = ContentStatus.MarkedForDeletion;
            file.UpdatedUtc = DateTime.UtcNow;
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(new { message = "File metadata changed. Refresh and retry." });
            }
        }
        return Accepted(new { file.Id, file.Status });
    }

    private static string ResolveContentType(string? value, string? fileName)
    {
        var candidate = value;
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            candidate = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
            {
                ".mp4" => "video/mp4",
                ".m4v" => "video/mp4",
                ".webm" => "video/webm",
                ".ogv" => "video/ogg",
                ".ogg" => "video/ogg",
                ".mov" => "video/quicktime",
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".wav" => "audio/wav",
                ".oga" => "audio/ogg",
                ".opus" => "audio/ogg",
                ".vtt" => "text/vtt",
                _ => candidate
            };
        }

        return SafeContentType(candidate);
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

    private FileDetails ToDetails(StoredFile file) => new(file.Id, ToLogicalFilePath(file.FilePath), file.FileName,
        file.ContentType, file.SizeBytes, file.Sha256, file.Status, file.UploadSource, file.OwnerId, file.Type,
        file.CreatedUtc, file.UpdatedUtc, Convert.ToBase64String(file.RowVersion));

    public sealed record FileListItem(Guid Id, string FilePath, string FileName, string ContentType, long SizeBytes,
        string Sha256, ContentStatus Status, FileUploadSource UploadSource, string? OwnerId, FileOwnerType Type,
        DateTime CreatedUtc, DateTime UpdatedUtc, string RowVersion, string ViewUrl);
    public sealed record FileListResponse(IReadOnlyList<FileListItem> Items, long Total, int Page, int PageSize);
    public sealed record FileDetails(Guid Id, string FilePath, string FileName, string ContentType, long SizeBytes,
        string Sha256, ContentStatus Status, FileUploadSource UploadSource, string? OwnerId, FileOwnerType Type,
        DateTime CreatedUtc, DateTime UpdatedUtc, string RowVersion);
    public sealed record RenameFileRequest([Required, StringLength(255)] string FileName, string? RowVersion);
}
