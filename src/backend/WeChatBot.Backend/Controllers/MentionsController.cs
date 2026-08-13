using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;
using WeChatBot.Backend.Services;

namespace WeChatBot.Backend.Controllers;

/// <summary>表示群提及事件写入后的授权决定和幂等状态。</summary>
/// <param name="Id">服务端事件主键。</param>
/// <param name="ExternalEventId">调用方提供的稳定事件标识。</param>
/// <param name="Decision">本次事件的处理决定。</param>
/// <param name="DecisionReason">不含敏感信息的决定原因。</param>
/// <param name="EntitlementId">允许处理时匹配到的权益主键。</param>
/// <param name="SuggestedMessage">需要告知用户的可选业务提示。</param>
/// <param name="Duplicate">是否为完全相同事件的幂等重放。</param>
public sealed record GroupMentionResponse(
    Guid Id,
    string ExternalEventId,
    MentionDecision Decision,
    string? DecisionReason,
    Guid? EntitlementId,
    string? SuggestedMessage,
    bool Duplicate);

/// <summary>提供管理员群提及查询和已认证 Agent 事件上报接口。</summary>
[ApiController]
[Route("api/group-mentions")]
public sealed class MentionsController(
    AppDbContext db,
    TenantContext tenant,
    TimeProvider timeProvider,
    EntitlementService entitlements,
    AgentControlService agents,
    AuditService audit,
    IAgentMutationSynchronization synchronization) : ControllerBase
{
    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<IReadOnlyList<GroupMentionEvent>>> List(
        [FromQuery] Guid? groupId,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 500) throw DomainException.Validation("invalid_page_size", "take must be between 1 and 500.");
        var query = db.GroupMentions.AsNoTracking();
        if (groupId is not null) query = query.Where(x => x.GroupId == groupId);
        return Ok(await query.OrderByDescending(x => x.CapturedAt).Take(take).ToListAsync(cancellationToken));
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public Task<ActionResult<GroupMentionResponse>> Ingest(
        GroupMentionRequest request,
        CancellationToken cancellationToken) =>
        IngestCore(request, cancellationToken);

    [HttpPost("/api/agents/{agentId}/group-mentions")]
    [Authorize(Roles = "Agent")]
    public Task<ActionResult<GroupMentionResponse>> IngestFromAgent(
        string agentId,
        AgentGroupMentionRequest request,
        CancellationToken cancellationToken) =>
        IngestFromAgentCore(agentId, request, cancellationToken);

    private async Task<ActionResult<GroupMentionResponse>> IngestFromAgentCore(
        string agentId,
        AgentGroupMentionRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginWriteTransactionAsync(cancellationToken);
        // 写锁早于身份复核，凭据轮换、吊销或恢复吊销只能在本次上报提交之后生效，消除校验后写入窗口。
        await agents.EnsureActiveBindingAsync(
            agentId,
            request.WeChatInstanceId,
            cancellationToken);
        await synchronization.AfterBindingValidatedAsync("group-mention.ingest", cancellationToken);
        var result = await IngestCore(request.Event, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// 开启 Agent 群提及上报的数据库写事务，并通过零行更新取得 SQLite 写锁。
    /// 管理员上报不依赖 Agent 凭据，因此继续使用原有短事务路径。
    /// </summary>
    /// <param name="cancellationToken">数据库事务取消令牌。</param>
    /// <returns>持有群提及表写锁的 EF Core 事务。</returns>
    private async Task<IDbContextTransaction> BeginWriteTransactionAsync(CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE GroupMentions SET CapturedAt = CapturedAt WHERE 0 = 1;",
                cancellationToken);
            return transaction;
        }
        catch
        {
            // 探针执行失败时事务尚未交给调用方，必须立即回滚并释放底层连接。
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            finally
            {
                await transaction.DisposeAsync();
            }

            throw;
        }
    }

    private async Task<ActionResult<GroupMentionResponse>> IngestCore(
        GroupMentionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.GroupId == Guid.Empty) throw DomainException.Validation("group_required", "GroupId is required.");
        var externalEventId = request.ExternalEventId.Trim();
        var existing = await db.GroupMentions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ExternalEventId == externalEventId, cancellationToken);
        if (existing is not null) return Ok(ToResponse(ValidateDuplicate(existing, request), true));

        var now = timeProvider.GetUtcNow();
        if (request.CapturedAt == default || request.CapturedAt < now.AddDays(-7) || request.CapturedAt > now.AddMinutes(5))
            throw DomainException.Validation("invalid_capture_time", "CapturedAt must be within the last seven days and no more than five minutes in the future.");

        _ = await db.Groups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.GroupId, cancellationToken)
            ?? throw DomainException.NotFound("Group");
        var (decision, reason, entitlement) = await DecideAsync(request, cancellationToken);
        var entity = new GroupMentionEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            ExternalEventId = externalEventId,
            GroupId = request.GroupId,
            SenderExternalId = request.SenderExternalId.Trim(),
            Content = request.Content,
            MentionedBot = request.MentionedBot,
            SenderIsBot = request.SenderIsBot,
            CapturedAt = request.CapturedAt,
            Decision = decision,
            DecisionReason = reason,
            EntitlementId = entitlement?.Id,
            CreatedAt = now
        };
        db.GroupMentions.Add(entity);
        var automationPaused = entity.Decision == MentionDecision.AutomationPaused;
        audit.Add(
            automationPaused ? "group-mention.automation-paused" : "group-mention.ingested",
            nameof(GroupMentionEvent),
            entity.Id.ToString("D"),
            !automationPaused,
            new
            {
                entity.ExternalEventId,
                entity.GroupId,
                entity.Decision
            });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await db.GroupMentions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ExternalEventId == externalEventId, cancellationToken);
            if (existing is null) throw;
            return Ok(ToResponse(ValidateDuplicate(existing, request), true));
        }

        return CreatedAtAction(nameof(Get), new { id = entity.Id }, ToResponse(entity, false));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<GroupMentionEvent>> Get(Guid id, CancellationToken cancellationToken) =>
        await db.GroupMentions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("Group mention event");

    private async Task<(MentionDecision Decision, string Reason, Entitlement? Entitlement)> DecideAsync(
        GroupMentionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SenderIsBot)
            return (MentionDecision.IgnoredBotMessage, "Messages sent by the bot cannot trigger another response.", null);
        if (!request.MentionedBot)
            return (MentionDecision.IgnoredNotMentioned, "The message did not mention the bot.", null);
        var entitlement = await entitlements.FindActiveWithFeatureAsync(
            ServiceTargetKind.Group,
            request.GroupId,
            WellKnownFeatures.GroupMention,
            cancellationToken: cancellationToken);
        if (entitlement is null)
            return (MentionDecision.ActivationRequired, "The group has no active entitlement granting group-mention.", null);
        var automationPaused = await db.Tenants.AsNoTracking()
            .Select(x => x.AutomationPaused)
            .SingleAsync(cancellationToken);
        return automationPaused
            ? (MentionDecision.AutomationPaused, "Automation is paused; downstream processing is disabled.", null)
            : (MentionDecision.Accepted, "The event is eligible for downstream rule processing.", entitlement);
    }

    private static GroupMentionEvent ValidateDuplicate(GroupMentionEvent existing, GroupMentionRequest request)
    {
        if (existing.GroupId != request.GroupId ||
            !string.Equals(existing.SenderExternalId, request.SenderExternalId.Trim(), StringComparison.Ordinal) ||
            !string.Equals(existing.Content, request.Content, StringComparison.Ordinal) ||
            existing.MentionedBot != request.MentionedBot ||
            existing.SenderIsBot != request.SenderIsBot ||
            existing.CapturedAt != request.CapturedAt)
        {
            throw DomainException.Conflict("event_id_reused", "ExternalEventId was already used for a different event payload.");
        }
        return existing;
    }

    private static GroupMentionResponse ToResponse(GroupMentionEvent entity, bool duplicate) => new(
        entity.Id,
        entity.ExternalEventId,
        entity.Decision,
        entity.DecisionReason,
        entity.EntitlementId,
        entity.Decision switch
        {
            MentionDecision.ActivationRequired => "服务尚未激活，请联系管理员获取激活码。",
            MentionDecision.AutomationPaused => "自动化服务已暂停，请稍后再试。",
            _ => null
        },
        duplicate);
}
