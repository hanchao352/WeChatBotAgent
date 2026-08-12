using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WeChatBot.Backend.Data;

namespace WeChatBot.Backend.Controllers;

[ApiController]
[Route("health")]
public sealed class HealthController(AppDbContext db) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("live")]
    public IActionResult Live() => Ok(new { status = "ok" });

    [Authorize]
    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken cancellationToken)
    {
        var databaseReady = await db.Database.CanConnectAsync(cancellationToken);
        return databaseReady
            ? Ok(new { status = "ready", database = "available" })
            : StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "not-ready", database = "unavailable" });
    }
}
