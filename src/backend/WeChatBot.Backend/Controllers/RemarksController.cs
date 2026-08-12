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
    TenantContext tenant,
    TimeProvider timeProvider,
    RemarkService remarkService,
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
        var existing = await db.RemarkTasks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null) return ValidateReplay(existing, requestHash);

        var preview = await remarkService.PreviewAsync(request.RuleId, request.TargetId, cancellationToken);
        var automationPaused = await db.Tenants.AsNoTracking()
            .Select(x => x.AutomationPaused)
            .SingleAsync(cancellationToken);
        if (automationPaused)
        {
            audit.Add(
                "remark-task.rejected.automation-paused",
                nameof(RemarkTask),
                request.TargetId.ToString("D"),
                false,
                new
                {
                    request.RuleId,
                    request.TargetId,
                    idempotencyKeyHash = StableHash.Sha256(idempotencyKey)
                });
            await db.SaveChangesAsync(cancellationToken);
            throw DomainException.Conflict(
                "automation_paused",
                "Automation is paused; new remark tasks cannot be created.");
        }
        await EnsureAutoRemarkEntitledAsync(
            preview.TargetKind,
            request.TargetId,
            request.TargetId,
            "remark-task.rejected.feature-required",
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        var task = new RemarkTask
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            RuleId = request.RuleId,
            TargetKind = preview.TargetKind,
            TargetId = request.TargetId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            GeneratedRemark = preview.GeneratedRemark,
            OriginalSystemRemark = preview.CurrentSystemRemark,
            OriginalWeChatRemark = preview.CurrentWeChatRemark,
            Status = preview.HasConflict ? RemarkTaskStatus.Conflict : RemarkTaskStatus.Pending,
            ConflictReason = preview.ConflictReason,
            CreatedAt = now
        };
        db.RemarkTasks.Add(task);
        audit.Add("remark-task.created", nameof(RemarkTask), task.Id.ToString("D"), details: new
        {
            task.RuleId,
            task.TargetKind,
            task.TargetId,
            task.Status
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await db.RemarkTasks.AsNoTracking()
                .SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existing is null) throw;
            return ValidateReplay(existing, requestHash);
        }
        return CreatedAtAction(nameof(Get), new { id = task.Id }, task);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RemarkTask>> Get(Guid id, CancellationToken cancellationToken) =>
        await db.RemarkTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("Remark task");

    [HttpPost("{id:guid}/complete")]
    public async Task<ActionResult<RemarkTask>> Complete(Guid id, RemarkTaskCompleteRequest request, CancellationToken cancellationToken)
    {
        var task = await db.RemarkTasks.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                   ?? throw DomainException.NotFound("Remark task");
        if (task.Version != request.ExpectedVersion)
            throw DomainException.Conflict("concurrency_conflict", "The remark task changed after it was read.");
        if (task.Status != RemarkTaskStatus.Pending)
            throw DomainException.Conflict("remark_task_not_pending", "Only pending remark tasks can be completed.");

        var now = timeProvider.GetUtcNow();
        if (request.Succeeded)
        {
            await EnsureAutoRemarkEntitledAsync(
                task.TargetKind,
                task.TargetId,
                task.Id,
                "remark-task.completion-rejected.feature-required",
                cancellationToken);
            if (!string.Equals(request.AppliedRemark, task.GeneratedRemark, StringComparison.Ordinal))
                throw DomainException.Validation("applied_remark_mismatch", "AppliedRemark must exactly match the generated remark.");
            await ApplySuccessfulRemarkAsync(task, now, cancellationToken);
            task.Status = RemarkTaskStatus.Completed;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.FailureReason))
                throw DomainException.Validation("failure_reason_required", "FailureReason is required when the task failed.");
            task.Status = RemarkTaskStatus.Failed;
            task.FailureReason = request.FailureReason.Trim();
        }
        task.CompletedAt = now;
        task.Version++;
        audit.Add("remark-task.completed", nameof(RemarkTask), task.Id.ToString("D"), request.Succeeded, new
        {
            task.Status,
            task.TargetKind,
            task.TargetId,
            task.FailureReason
        });
        await db.SaveChangesAsync(cancellationToken);
        return task;
    }

    private async Task ApplySuccessfulRemarkAsync(RemarkTask task, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (task.TargetKind == ServiceTargetKind.Contact)
        {
            var contact = await db.Contacts.SingleOrDefaultAsync(x => x.Id == task.TargetId, cancellationToken)
                          ?? throw DomainException.NotFound("Contact");
            if (contact.ManualRemarkProtected)
                throw DomainException.Conflict("remark_now_protected", "Manual remark protection was enabled after the task was created.");
            await EnsureRemarkSnapshotUnchangedAsync(
                task,
                contact.SystemRemark,
                contact.CurrentWeChatRemark,
                cancellationToken);
            contact.SystemRemark = task.GeneratedRemark;
            contact.CurrentWeChatRemark = task.GeneratedRemark;
            contact.UpdatedAt = now;
            contact.Version++;
        }
        else if (task.TargetKind == ServiceTargetKind.Group)
        {
            var group = await db.Groups.SingleOrDefaultAsync(x => x.Id == task.TargetId, cancellationToken)
                        ?? throw DomainException.NotFound("Group");
            if (group.ManualRemarkProtected)
                throw DomainException.Conflict("remark_now_protected", "Manual remark protection was enabled after the task was created.");
            await EnsureRemarkSnapshotUnchangedAsync(
                task,
                group.SystemRemark,
                group.CurrentWeChatRemark,
                cancellationToken);
            group.SystemRemark = task.GeneratedRemark;
            group.CurrentWeChatRemark = task.GeneratedRemark;
            group.UpdatedAt = now;
            group.Version++;
        }
    }

    private async Task EnsureRemarkSnapshotUnchangedAsync(
        RemarkTask task,
        string? currentSystemRemark,
        string? currentWeChatRemark,
        CancellationToken cancellationToken)
    {
        var systemRemarkChanged = !string.Equals(
            task.OriginalSystemRemark,
            currentSystemRemark,
            StringComparison.Ordinal);
        var weChatRemarkChanged = !string.Equals(
            task.OriginalWeChatRemark,
            currentWeChatRemark,
            StringComparison.Ordinal);
        if (!systemRemarkChanged && !weChatRemarkChanged) return;

        audit.Add(
            "remark-task.rejected.target-changed",
            nameof(RemarkTask),
            task.Id.ToString("D"),
            false,
            new
            {
                task.TargetKind,
                task.TargetId,
                systemRemarkChanged,
                weChatRemarkChanged
            });
        await db.SaveChangesAsync(cancellationToken);
        throw DomainException.Conflict(
            "remark_target_changed",
            "The target remarks changed after this task was created; create a new task from the current state.");
    }

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
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            throw DomainException.Validation("invalid_idempotency_key", "Idempotency-Key is required and must be at most 128 characters.");
        return value.Trim();
    }

    private static RemarkTask ValidateReplay(RemarkTask task, string requestHash)
    {
        if (!string.Equals(task.RequestHash, requestHash, StringComparison.Ordinal))
            throw DomainException.Conflict("idempotency_key_reused", "The Idempotency-Key was already used for a different request.");
        return task;
    }
}
