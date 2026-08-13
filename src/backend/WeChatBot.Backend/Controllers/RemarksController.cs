using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;
using WeChatBot.Backend.Services;

namespace WeChatBot.Backend.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/remark-rules")]
public sealed class RemarkRulesController(
    AppDbContext db,
    TenantContext tenant,
    TimeProvider timeProvider,
    RemarkService remarkService,
    AuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RemarkRule>>> List(CancellationToken cancellationToken) =>
        Ok(await db.RemarkRules.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RemarkRule>> Get(Guid id, CancellationToken cancellationToken) =>
        await db.RemarkRules.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("Remark rule");

    [HttpPost]
    public async Task<ActionResult<RemarkRule>> Create(RemarkRuleCreateRequest request, CancellationToken cancellationToken)
    {
        remarkService.ValidateTemplate(request.TargetKind, request.Template, request.MaxLength);
        var now = timeProvider.GetUtcNow();
        var entity = new RemarkRule
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            Name = request.Name.Trim(),
            TargetKind = request.TargetKind,
            Template = request.Template.Trim(),
            ConflictPolicy = request.ConflictPolicy,
            IsEnabled = request.IsEnabled,
            MaxLength = request.MaxLength,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.RemarkRules.Add(entity);
        audit.Add("remark-rule.created", nameof(RemarkRule), entity.Id.ToString("D"), details: new
        {
            entity.Name,
            entity.TargetKind,
            entity.ConflictPolicy
        });
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, entity);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<RemarkRule>> Update(Guid id, RemarkRuleUpdateRequest request, CancellationToken cancellationToken)
    {
        remarkService.ValidateTemplate(request.TargetKind, request.Template, request.MaxLength);
        var entity = await db.RemarkRules.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                     ?? throw DomainException.NotFound("Remark rule");
        if (entity.Version != request.ExpectedVersion)
            throw DomainException.Conflict("concurrency_conflict", "The remark rule changed after it was read.");

        entity.Name = request.Name.Trim();
        entity.TargetKind = request.TargetKind;
        entity.Template = request.Template.Trim();
        entity.ConflictPolicy = request.ConflictPolicy;
        entity.IsEnabled = request.IsEnabled;
        entity.MaxLength = request.MaxLength;
        entity.UpdatedAt = timeProvider.GetUtcNow();
        entity.Version++;
        audit.Add("remark-rule.updated", nameof(RemarkRule), entity.Id.ToString("D"), details: new
        {
            entity.Name,
            entity.Version,
            entity.IsEnabled
        });
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/remark-tasks")]
public sealed class RemarkTasksController(
    AppDbContext db,
    RemarkService remarkService,
    RemarkTaskLeaseService leases,
    EntitlementService entitlements,
    AuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RemarkTask>>> List(
        [FromQuery] RemarkTaskStatus? status,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 500) throw DomainException.Validation("invalid_page_size", "take must be between 1 and 500.");
        var query = db.RemarkTasks.AsNoTracking();
        if (status is not null) query = query.Where(x => x.Status == status);
        return Ok(await query.OrderByDescending(x => x.CreatedAt).Take(take).ToListAsync(cancellationToken));
    }

    [HttpPost("preview")]
    public async Task<ActionResult<RemarkPreview>> Preview(
        RemarkTaskRequest request,
        CancellationToken cancellationToken)
    {
        var preview = await remarkService.PreviewAsync(request.RuleId, request.TargetId, cancellationToken);
        var automationPaused = await db.Tenants.AsNoTracking()
            .Select(x => x.AutomationPaused)
            .SingleAsync(cancellationToken);
        if (automationPaused)
        {
            audit.Add(
                "remark-task.preview-rejected.automation-paused",
                nameof(RemarkTask),
                request.TargetId.ToString("D"),
                false,
                new
                {
                    request.RuleId,
                    request.TargetId
                });
            await db.SaveChangesAsync(cancellationToken);
            throw DomainException.Conflict(
                "automation_paused",
                "Automation is paused; remark previews are unavailable.");
        }

        await EnsureAutoRemarkEntitledAsync(
            preview.TargetKind,
            request.TargetId,
            request.TargetId,
            "remark-task.preview-rejected.feature-required",
            cancellationToken);
        return preview;
    }

    [HttpPost]
    public async Task<ActionResult<RemarkTask>> Create(
        RemarkTaskRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        idempotencyKey = ValidateIdempotencyKey(idempotencyKey);
        var requestHash = StableHash.Sha256($"{request.RuleId:N}|{request.TargetId:N}");
        var result = await leases.CreateAdministrativelyAsync(
            request,
            idempotencyKey,
            requestHash,
            cancellationToken);
        return result.Replayed
            ? Ok(result.Task)
            : CreatedAtAction(nameof(Get), new { id = result.Task.Id }, result.Task);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RemarkTask>> Get(Guid id, CancellationToken cancellationToken) =>
        await db.RemarkTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("Remark task");

    /// <summary>
    /// 由管理员在没有活动 Agent 租约时提交备注任务终态，并在成功路径复用统一业务校验。
    /// 已过期租约允许接管，但无论成功或失败都会清除旧租约凭据。
    /// </summary>
    /// <param name="id">待完成的备注任务标识。</param>
    /// <param name="request">包含期望版本、执行结果以及互斥结果字段的管理员请求。</param>
    /// <param name="cancellationToken">数据库查询和保存操作的取消令牌。</param>
    /// <returns>写入终态、完成时刻和新版本后的备注任务。</returns>
    [HttpPost("{id:guid}/complete")]
    public async Task<ActionResult<RemarkTask>> Complete(
        Guid id,
        RemarkTaskCompleteRequest request,
        CancellationToken cancellationToken) =>
        await leases.CompleteAdministrativelyAsync(id, request, cancellationToken);

    private async Task EnsureAutoRemarkEntitledAsync(
        ServiceTargetKind targetKind,
        Guid targetId,
        Guid auditResourceId,
        string auditAction,
        CancellationToken cancellationToken)
    {
        var entitlement = await entitlements.FindActiveWithFeatureAsync(
            targetKind,
            targetId,
            WellKnownFeatures.AutoRemark,
            cancellationToken: cancellationToken);
        if (entitlement is not null) return;

        audit.Add(
            auditAction,
            nameof(RemarkTask),
            auditResourceId.ToString("D"),
            false,
            new
            {
                targetKind,
                targetId,
                requiredFeature = WellKnownFeatures.AutoRemark
            });
        await db.SaveChangesAsync(cancellationToken);
        throw DomainException.Conflict(
            "auto_remark_feature_required",
            "The target has no active entitlement granting auto-remark.");
    }

    private static string ValidateIdempotencyKey(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 128)
            throw DomainException.Validation("invalid_idempotency_key", "Idempotency-Key is required and must be at most 128 characters.");
        return normalized;
    }

}
