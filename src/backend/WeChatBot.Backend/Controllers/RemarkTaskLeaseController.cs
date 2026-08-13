using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Services;

namespace WeChatBot.Backend.Controllers;

/// <summary>
/// 暴露仅供已验证 Agent 使用的备注任务租约协议，不包含任何真实微信 UI 操作实现。
/// </summary>
[ApiController]
[Authorize(Roles = "Agent")]
[Route("api/agents/{agentId}/remark-tasks")]
public sealed class RemarkTaskLeaseController(RemarkTaskLeaseService leases) : ControllerBase
{
    /// <summary>
    /// 原子领取当前租户中最早可用的待处理任务；队列为空时返回 204。
    /// </summary>
    /// <param name="agentId">已注册的 Agent 标识。</param>
    /// <param name="request">当前微信实例绑定。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>含一次性明文租约令牌的任务，或空队列响应。</returns>
    [HttpPost("claim")]
    [ProducesResponseType<RemarkTaskLeaseResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<RemarkTaskLeaseResponse>> Claim(
        string agentId,
        RemarkTaskClaimRequest request,
        CancellationToken cancellationToken)
    {
        var claimed = await leases.ClaimAsync(agentId, request, cancellationToken);
        return claimed is null ? NoContent() : Ok(claimed);
    }

    /// <summary>
    /// 续租仍由当前 Agent 和微信实例持有且尚未过期的任务。
    /// </summary>
    /// <param name="agentId">已注册的 Agent 标识。</param>
    /// <param name="taskId">待续租任务标识。</param>
    /// <param name="request">租约令牌、实例绑定和期望版本。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>更新后的租约快照。</returns>
    [HttpPost("{taskId:guid}/renew")]
    [ProducesResponseType<RemarkTaskLeaseResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RemarkTaskLeaseResponse>> Renew(
        string agentId,
        Guid taskId,
        RemarkTaskLeaseRequest request,
        CancellationToken cancellationToken) =>
        Ok(await leases.RenewAsync(taskId, agentId, request, cancellationToken));

    /// <summary>
    /// 主动释放有效租约，使待处理任务可立即由任意健康 Agent 重新认领。
    /// </summary>
    /// <param name="agentId">已注册的 Agent 标识。</param>
    /// <param name="taskId">待释放任务标识。</param>
    /// <param name="request">租约令牌、实例绑定和期望版本。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>释放后的任务状态和版本。</returns>
    [HttpPost("{taskId:guid}/release")]
    [ProducesResponseType<RemarkTaskLeaseReleaseResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RemarkTaskLeaseReleaseResponse>> Release(
        string agentId,
        Guid taskId,
        RemarkTaskLeaseRequest request,
        CancellationToken cancellationToken) =>
        Ok(await leases.ReleaseAsync(taskId, agentId, request, cancellationToken));

    /// <summary>
    /// 在持有有效租约时幂等提交成功或失败结果，并使任务进入终态。
    /// </summary>
    /// <param name="agentId">已注册的 Agent 标识。</param>
    /// <param name="taskId">待完成任务标识。</param>
    /// <param name="request">租约证明和最终执行结果。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>稳定的最终结果响应。</returns>
    [HttpPost("{taskId:guid}/complete")]
    [ProducesResponseType<RemarkTaskLeaseCompletionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RemarkTaskLeaseCompletionResponse>> Complete(
        string agentId,
        Guid taskId,
        RemarkTaskLeaseCompleteRequest request,
        CancellationToken cancellationToken) =>
        Ok(await leases.CompleteAsync(taskId, agentId, request, cancellationToken));
}
