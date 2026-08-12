using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;
using WeChatBot.Backend.Services;

namespace WeChatBot.Backend.Controllers;

public sealed record GroupMentionResponse(
    Guid Id,
    string ExternalEventId,
    MentionDecision Decision,
    string? DecisionReason,
    Guid? EntitlementId,
    string? SuggestedMessage,
    bool Duplicate);

[ApiController]
[Route("api/group-mentions")]
public sealed class MentionsController(
    AppDbContext db,
    TenantContext tenant,
    TimeProvider timeProvider,
    EntitlementService entitlements,
    AgentControlService agents,
    AuditService audit) : ControllerBase
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
        await agents.EnsureActiveBindingAsync(
            agentId,
            request.WeChatInstanceId,
            cancellationToken);
        return await IngestCore(request.Event, cancellationToken);
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
