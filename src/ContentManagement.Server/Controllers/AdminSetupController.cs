using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/admin/setup")]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Roles = "Administrator")]
public sealed class AdminSetupController(
    ContentManagementDbContext db,
    IDataProtectionProvider protectionProvider,
    ILogger<AdminSetupController> logger) : ControllerBase
{
    private readonly IDataProtector _apiKeyProtector =
        protectionProvider.CreateProtector("ContentManagement.ExternalFileApi.ApiKey.v1");

    [HttpGet]
    public async Task<ActionResult<SetupResponse>> Get(CancellationToken cancellationToken)
    {
        var settings = await GetOrCreateSettingsAsync(cancellationToken);
        var used = await GetExternalUsedBytesAsync(cancellationToken);
        return Ok(ToResponse(settings, used));
    }

    [HttpPut]
    public async Task<ActionResult<SetupResponse>> Save([FromBody] UpdateSetupRequest request, CancellationToken cancellationToken)
    {
        if (request.StaleFileAgeDays is < 1 or > 3650)
            return BadRequest(new { message = "File age threshold must be between 1 and 3650 days." });
        if (request.ExternalStorageQuotaBytes < 0)
            return BadRequest(new { message = "External storage quota must be zero or greater." });

        var endpoint = request.ExternalStorageBaseUrl?.Trim().TrimEnd('/') ?? "";
        if (!string.IsNullOrWhiteSpace(endpoint) &&
            (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
             (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)))
            return BadRequest(new { message = "External API URL must be an absolute HTTPS URL (HTTP is allowed only for localhost)." });

        var settings = await GetOrCreateSettingsAsync(cancellationToken);
        settings.StaleFileAgeDays = request.StaleFileAgeDays;
        settings.ExternalStorageBaseUrl = endpoint;
        settings.ExternalStorageQuotaBytes = request.ExternalStorageQuotaBytes;
        settings.ExternalStoragePreferred = request.ExternalStoragePreferred;
        if (!string.IsNullOrWhiteSpace(request.ExternalStorageApiKey))
            settings.ExternalStorageApiKeyProtected = _apiKeyProtector.Protect(request.ExternalStorageApiKey.Trim());
        settings.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Administrator updated system setup and external storage preferences.");
        var used = await GetExternalUsedBytesAsync(cancellationToken);
        return Ok(ToResponse(settings, used));
    }

    private async Task<SystemSettings> GetOrCreateSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await db.SystemSettings.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings is not null)
            return settings;

        settings = new SystemSettings { Id = 1 };
        db.SystemSettings.Add(settings);
        await db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private async Task<long> GetExternalUsedBytesAsync(CancellationToken cancellationToken) =>
        await db.Files.AsNoTracking()
            .Where(file => file.UploadSource == FileUploadSource.ExternalServer &&
                           file.Status != ContentStatus.Deleted)
            .Select(file => (long?)file.SizeBytes)
            .SumAsync(cancellationToken) ?? 0L;

    private static SetupResponse ToResponse(SystemSettings settings, long usedBytes) =>
        new(settings.StaleFileAgeDays, settings.ExternalStorageBaseUrl,
            !string.IsNullOrWhiteSpace(settings.ExternalStorageApiKeyProtected),
            settings.ExternalStorageQuotaBytes, usedBytes,
            Math.Max(0, settings.ExternalStorageQuotaBytes - usedBytes),
            settings.ExternalStoragePreferred);

    public sealed record UpdateSetupRequest(
        int StaleFileAgeDays,
        string? ExternalStorageBaseUrl,
        string? ExternalStorageApiKey,
        long ExternalStorageQuotaBytes,
        bool ExternalStoragePreferred);

    public sealed record SetupResponse(
        int StaleFileAgeDays,
        string ExternalStorageBaseUrl,
        bool ExternalStorageApiKeyConfigured,
        long ExternalStorageQuotaBytes,
        long ExternalStorageUsedBytes,
        long ExternalStorageRemainingBytes,
        bool ExternalStoragePreferred);
}
