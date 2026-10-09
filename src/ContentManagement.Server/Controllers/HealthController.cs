using Microsoft.AspNetCore.Mvc;
namespace ContentManagement.Server.Controllers;
[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> Get() => Ok(new HealthResponse("Healthy"));
}
public sealed record HealthResponse(string Status);
