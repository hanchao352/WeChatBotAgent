using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Services;

namespace WeChatBot.Backend.Controllers;

[ApiController]
[Route("api/agents")]
public sealed class AgentsController(AgentControlService agents) : ControllerBase
{
    [HttpPost("heartbeat")]
    [Authorize(Roles = "Agent")]
    [ProducesResponseType<AgentHeartbeatResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AgentHeartbeatResponse>> Heartbeat(
        AgentHeartbeatRequest request,
        CancellationToken cancellationToken) =>
        Ok(await agents.RecordHeartbeatAsync(request, cancellationToken));

    [HttpGet]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType<IReadOnlyList<AgentListItem>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AgentListItem>>> List(CancellationToken cancellationToken) =>
        Ok(await agents.ListAsync(cancellationToken));
}
