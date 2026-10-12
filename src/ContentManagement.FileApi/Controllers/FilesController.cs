using ContentManagement.FileApi.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ContentManagement.FileApi.Configuration;

namespace ContentManagement.FileApi.Controllers;

[ApiController]
[Route("api/files")]
[Authorize]
public sealed class FilesController(
    FileStorageService storage,
    IOptions<FileApiOptions> options) : ControllerBase
{
    /// <summary>Uploads a binary file to the configured storage root using a relative filePath.</summary>
    [HttpPut]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<StoredFileInfo>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<StoredFileInfo>> Upload(
        [FromQuery] string filePath,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length <= 0)
            return BadRequest(new { message = "A non-empty file is required." });

        if (file.Length > options.Value.MaxUploadBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { message = "File exceeds the configured upload limit." });

        try
        {
            await using var source = file.OpenReadStream();
            var result = await storage.SaveAsync(filePath, source, cancellationToken);
            return Created($"/api/files/download?filePath={Uri.EscapeDataString(filePath)}", result);
        }
        catch (FileSizeLimitExceededException exception)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { message = exception.Message });
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (FileAlreadyExistsException)
        {
            return Conflict(new { message = "A file already exists at the requested filePath." });
        }
    }

    /// <summary>Downloads a file as an attachment.</summary>
    [HttpGet("download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Download([FromQuery] string filePath)
    {
        try
        {
            var fullPath = storage.GetExistingFilePath(filePath);
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            var downloadName = Path.GetFileName(fullPath);
            return PhysicalFile(fullPath, "application/octet-stream", downloadName, enableRangeProcessing: true);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    /// <summary>Streams a browser-safe file inline. Active content such as HTML and SVG is not rendered.</summary>
    [HttpGet("view")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public IActionResult ViewFile([FromQuery] string filePath)
    {
        try
        {
            var fullPath = storage.GetExistingFilePath(filePath);
            var contentType = storage.GetContentType(fullPath);
            if (!FileStorageService.CanViewInline(contentType))
            {
                return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                    new { message = "This file type is not supported for inline viewing. Use the download endpoint." });
            }

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Content-Disposition"] = $"inline; filename*=UTF-8''{Uri.EscapeDataString(Path.GetFileName(fullPath))}";
            return PhysicalFile(fullPath, contentType, enableRangeProcessing: true);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }
    /// <summary>Deletes a file by its relative storage path. Repeated deletes are safe.</summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Delete([FromQuery] string filePath)
    {
        try
        {
            storage.Delete(filePath);
            return NoContent();
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

}
