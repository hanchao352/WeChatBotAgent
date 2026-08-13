using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Services;

namespace WeChatBot.Backend.Controllers;

/// <summary>提供 Agent 预注册、独立凭据生命周期、心跳和只读列表管理接口。</summary>
[ApiController]
[Route("api/agents")]
public sealed class AgentsController(AgentControlService agents) : ControllerBase
{
    /// <summary>预注册 Agent，并仅在本次响应中返回首次独立凭据。</summary>
    /// <param name="request">不可变 Agent 与微信实例绑定。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>注册视图及一次性明文凭据。</returns>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType<AgentCredentialIssueResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AgentCredentialIssueResponse>> Register(
        RegisterAgentRequest request,
        CancellationToken cancellationToken)
    {
        var registration = await agents.RegisterAsync(request, cancellationToken);
        return CreatedAtAction(nameof(List), registration);
    }

    /// <summary>轮换指定注册的凭据，使旧凭据立即失效并一次性返回新凭据。</summary>
    /// <param name="registrationId">AgentRegistration 主键。</param>
    /// <param name="request">乐观并发版本。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>更新后的注册视图和新明文凭据。</returns>
    [HttpPost("{registrationId:guid}/credential/rotate")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType<AgentCredentialIssueResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AgentCredentialIssueResponse>> RotateCredential(
        Guid registrationId,
        AgentCredentialVersionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await agents.RotateCredentialAsync(registrationId, request, cancellationToken));

    /// <summary>吊销指定注册的当前凭据，后续请求必须先由管理员重新轮换签发。</summary>
    /// <param name="registrationId">AgentRegistration 主键。</param>
    /// <param name="request">乐观并发版本。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>不含任何凭据材料的更新后注册视图。</returns>
    [HttpPost("{registrationId:guid}/credential/revoke")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType<AgentListItem>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AgentListItem>> RevokeCredential(
        Guid registrationId,
        AgentCredentialVersionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await agents.RevokeCredentialAsync(registrationId, request, cancellationToken));

    /// <summary>记录与独立认证身份完全一致的 Agent 心跳。</summary>
    /// <param name="request">Agent 运行状态和绑定身份。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>服务端是否接受当前 dry-run 在线租约。</returns>
    [HttpPost("heartbeat")]
    [Authorize(Roles = "Agent")]
    [ProducesResponseType<AgentHeartbeatResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AgentHeartbeatResponse>> Heartbeat(
        AgentHeartbeatRequest request,
        CancellationToken cancellationToken) =>
        Ok(await agents.RecordHeartbeatAsync(request, cancellationToken));

    /// <summary>列出不含凭据明文或摘要的 Agent 注册及在线状态。</summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>租户内 Agent 安全列表视图。</returns>
    [HttpGet]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType<IReadOnlyList<AgentListItem>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AgentListItem>>> List(CancellationToken cancellationToken) =>
        Ok(await agents.ListAsync(cancellationToken));
}
