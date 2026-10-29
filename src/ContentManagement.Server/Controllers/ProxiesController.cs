using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ContentManagement.Server.Data;
using ContentManagement.Server.Domain;
using ContentManagement.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.Controllers;

[ApiController]
[Route("api/proxies")]
[Authorize(Roles = "Administrator")]
public sealed class ProxiesController(
    ProxyDirectoryClient proxyDirectory,
    ILogger<ProxiesController> logger,
    ContentManagementDbContext db,
    PinnedProxyCache pinnedProxyCache) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ProxyListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<ProxyListResponse>> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await proxyDirectory.GetAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception, "ProxyScrape request timed out.");
            return Problem(statusCode: StatusCodes.Status504GatewayTimeout,
                title: "Proxy provider timed out",
                detail: "The proxy provider did not respond in time. Please retry.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "ProxyScrape request failed.");
            return Problem(statusCode: StatusCodes.Status502BadGateway,
                title: "Proxy provider unavailable",
                detail: "The proxy provider could not be reached. Please retry later.");
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "ProxyScrape returned invalid JSON.");
            return Problem(statusCode: StatusCodes.Status502BadGateway,
                title: "Invalid proxy provider response",
                detail: "The proxy provider returned data that could not be parsed.");
        }
        catch (ProxyDirectoryException exception)
        {
            logger.LogWarning(exception, "ProxyScrape returned an unusable response.");
            return Problem(statusCode: StatusCodes.Status502BadGateway,
                title: "Proxy provider unavailable",
                detail: exception.Message);
        }
    }

    [HttpGet("pinned")]
    public async Task<ActionResult<PinnedProxySnapshot?>> GetPinnedAsync(CancellationToken cancellationToken)
    {
        var pinned = await db.PinnedProxies.AsNoTracking().SingleOrDefaultAsync(proxy => proxy.Id == 1, cancellationToken);
        if (pinned is null) { pinnedProxyCache.Clear(); return Ok((PinnedProxySnapshot?)null); }
        var snapshot = new PinnedProxySnapshot(pinned.Address, pinned.Protocol, pinned.PinnedUtc, pinned.LastCheckedUtc, pinned.LatencyMilliseconds, pinned.IsHealthy);
        pinnedProxyCache.Set(snapshot);
        return Ok(snapshot);
    }

    [HttpPost("pin")]
    public async Task<ActionResult<PinnedProxySnapshot>> PinAsync([FromBody] PinProxyRequest request, CancellationToken cancellationToken)
    {
        if (!ProxyAddressRules.TryNormalize(request.Address, out var address, out var protocol) ||
            !IPAddress.TryParse(new Uri(address).Host, out var ip) || !ProxyAddressRules.IsPublicAddress(ip))
            return BadRequest(new ProblemDetails { Title = "Invalid proxy address", Detail = "Choose a proxy with a public IP address from the list." });

        var pinned = await db.PinnedProxies.SingleOrDefaultAsync(proxy => proxy.Id == 1, cancellationToken);
        var now = DateTime.UtcNow;
        if (pinned is null)
        {
            pinned = new PinnedProxy { Id = 1, Address = address, Protocol = protocol, PinnedUtc = now };
            db.PinnedProxies.Add(pinned);
        }
        else
        {
            pinned.Address = address; pinned.Protocol = protocol; pinned.PinnedUtc = now;
            pinned.LastCheckedUtc = null; pinned.LatencyMilliseconds = null; pinned.IsHealthy = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        var snapshot = new PinnedProxySnapshot(pinned.Address, pinned.Protocol, pinned.PinnedUtc, pinned.LastCheckedUtc, pinned.LatencyMilliseconds, pinned.IsHealthy);
        pinnedProxyCache.Set(snapshot);
        return Ok(snapshot);
    }

    public sealed record PinProxyRequest(string Address);
}
