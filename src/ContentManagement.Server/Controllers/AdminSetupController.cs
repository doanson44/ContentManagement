using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/admin/setup")]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Roles = "Administrator")]
public sealed class AdminSetupController(
    ContentManagementDbContext db,
    ILogger<AdminSetupController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SetupResponse>> Get(CancellationToken cancellationToken)
    {
        var settings = await GetOrCreateSettingsAsync(cancellationToken);
        return Ok(ToResponse(settings));
    }

    [HttpPut]
    public async Task<ActionResult<SetupResponse>> Save([FromBody] UpdateSetupRequest request, CancellationToken cancellationToken)
    {
        if (request.StaleFileAgeDays is < 1 or > 3650 ||
            request.CleanupIntervalHours is < 1 or > 24 ||
            request.DeletionGracePeriodDays is < 1 or > 90)
            return BadRequest(new { message = "Cleanup values are outside the allowed range." });

        var settings = await GetOrCreateSettingsAsync(cancellationToken);
        settings.StaleFileAgeDays = request.StaleFileAgeDays;
        settings.CleanupIntervalHours = request.CleanupIntervalHours;
        settings.DeletionGracePeriodDays = request.DeletionGracePeriodDays;
        settings.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Administrator updated stale-file cleanup settings.");
        return Ok(ToResponse(settings));
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

    private static SetupResponse ToResponse(SystemSettings settings) =>
        new(settings.StaleFileAgeDays, settings.CleanupIntervalHours,
            settings.DeletionGracePeriodDays, settings.UpdatedUtc);

    public sealed record UpdateSetupRequest(
        int StaleFileAgeDays,
        int CleanupIntervalHours,
        int DeletionGracePeriodDays);

    public sealed record SetupResponse(
        int StaleFileAgeDays,
        int CleanupIntervalHours,
        int DeletionGracePeriodDays,
        DateTime UpdatedUtc);
}