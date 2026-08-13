using System.Security.Cryptography;
using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;

namespace WeChatBot.Backend.Services;

/// <summary>
/// 定义备注任务租约的服务端有效期配置。
/// </summary>
public sealed class RemarkTaskLeaseOptions
{
    /// <summary>获取或设置每次认领或续租授予的秒数，允许范围为 15 到 300 秒。</summary>
    public int DurationSeconds { get; set; } = 60;
}

/// <summary>表示管理员创建备注任务的持久化结果以及是否命中幂等重放。</summary>
/// <param name="Task">创建或重放读取到的备注任务。</param>
/// <param name="Replayed">命中同一幂等键和相同请求载荷时为真。</param>
public sealed record AdministrativeRemarkTaskCreationResult(
    RemarkTask Task,
    bool Replayed);

/// <summary>
/// 提供备注任务的原子认领、续租、释放和幂等完成协议。
/// </summary>
public sealed class RemarkTaskLeaseService(
    AppDbContext db,
    TenantContext tenant,
    TimeProvider timeProvider,
    AgentControlService agents,
    RemarkService remarks,
    AuditService audit,
    IOptions<RemarkTaskLeaseOptions> options,
    IAgentMutationSynchronization synchronization,
    IRemarkTaskMutationSynchronization remarkTaskSynchronization)
{
    /// <summary>数据库忙或候选任务被并发抢占时允许的有限重试次数。</summary>
    private const int MaximumClaimAttempts = 8;

    /// <summary>随机租约令牌的字节长度，提供 256 位不可猜测熵。</summary>
    private const int LeaseTokenBytes = 32;

    /// <summary>配置允许的最短租约时长，防止心跳抖动造成无意义的频繁重领。</summary>
    private const int MinimumLeaseSeconds = 15;

    /// <summary>配置允许的最长租约时长，限制异常 Agent 独占任务的恢复时间。</summary>
    private const int MaximumLeaseSeconds = 300;

    /// <summary>获取经启动期校验约束后的租约时长。</summary>
    private TimeSpan LeaseDuration => TimeSpan.FromSeconds(options.Value.DurationSeconds);

    /// <summary>
    /// 校验 Agent 绑定并原子认领队列中最早可用的备注任务。
    /// </summary>
    /// <param name="agentId">路由提供的 Agent 标识。</param>
    /// <param name="request">包含微信实例绑定的领取请求。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>有任务时返回含明文租约令牌的响应；队列为空时返回空值。</returns>
    public async Task<RemarkTaskLeaseResponse?> ClaimAsync(
        string agentId,
        RemarkTaskClaimRequest request,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration();
        var normalizedAgentId = AgentControlService.NormalizeAgentId(agentId);
        var instanceId = request.WeChatInstanceId.Trim();

        for (var attempt = 0; attempt < MaximumClaimAttempts; attempt++)
        {
            try
            {
                await using var transaction = await BeginWriteTransactionAsync(cancellationToken);
                db.ChangeTracker.Clear();
                await agents.EnsureActiveBindingAsync(agentId, instanceId, cancellationToken);
                await synchronization.AfterBindingValidatedAsync("remark-task.claim", cancellationToken);
                var now = timeProvider.GetUtcNow();
                var candidate = await db.RemarkTasks.AsNoTracking()
                    .Where(task =>
                        task.TenantId == tenant.TenantId &&
                        task.Status == RemarkTaskStatus.Pending &&
                        task.TargetExternalId != "" &&
                        task.ExpectedTargetDisplayName != "" &&
                        (task.LeaseExpiresAt == null || task.LeaseExpiresAt <= now))
                    .OrderBy(task => task.CreatedAt)
                    .ThenBy(task => task.Id)
                    .Select(task => new { task.Id, task.Version })
                    .FirstOrDefaultAsync(cancellationToken);
                if (candidate is null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return null;
                }

                var leaseToken = CreateLeaseToken();
                var tokenHash = HashLeaseToken(leaseToken);
                var expiresAt = now.Add(LeaseDuration);

                // ExecuteUpdate 绕过跟踪器和 SaveChanges，因此必须显式包含租户、状态、过期和版本条件。
                var affected = await db.RemarkTasks
                    .Where(task =>
                        task.TenantId == tenant.TenantId &&
                        task.Id == candidate.Id &&
                        task.Status == RemarkTaskStatus.Pending &&
                        task.TargetExternalId != "" &&
                        task.ExpectedTargetDisplayName != "" &&
                        task.Version == candidate.Version &&
                        (task.LeaseExpiresAt == null || task.LeaseExpiresAt <= now))
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(task => task.ClaimedByAgentId, normalizedAgentId)
                        .SetProperty(task => task.ClaimedWeChatInstanceId, instanceId)
                        .SetProperty(task => task.LeaseTokenHash, tokenHash)
                        .SetProperty(task => task.LeaseExpiresAt, expiresAt)
                        .SetProperty(task => task.AttemptCount, task => task.AttemptCount + 1)
                        .SetProperty(task => task.Version, task => task.Version + 1), cancellationToken);
                if (affected != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    continue;
                }

                var claimed = await db.RemarkTasks.AsNoTracking()
                    .SingleAsync(task => task.Id == candidate.Id, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return ToLeaseResponse(claimed, leaseToken);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                // SQLite 只有单写者；短暂退避避免并发领取把可恢复的写锁竞争暴露为 500。
                db.ChangeTracker.Clear();
                if (attempt < MaximumClaimAttempts - 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(15 * (attempt + 1)), cancellationToken);
                }
            }
        }

        throw DomainException.Conflict(
            "remark_task_claim_busy",
            "Remark task claiming is temporarily busy; retry the request.");
    }

    /// <summary>
    /// 在租约仍有效且持有证明完全匹配时延长租约。
    /// </summary>
    /// <param name="taskId">待续租的任务标识。</param>
    /// <param name="agentId">路由提供的 Agent 标识。</param>
    /// <param name="request">租约持有证明和期望版本。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>新的租约到期时间和任务版本。</returns>
    public async Task<RemarkTaskLeaseResponse> RenewAsync(
        Guid taskId,
        string agentId,
        RemarkTaskLeaseRequest request,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration();
        await using var transaction = await BeginWriteTransactionAsync(cancellationToken);
        var identity = await ValidateActiveLeaseRequestAsync(agentId, request.WeChatInstanceId, request.LeaseToken, cancellationToken);
        await synchronization.AfterBindingValidatedAsync("remark-task.renew", cancellationToken);
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.Add(LeaseDuration);

        var affected = await LeaseOwnerQuery(
                taskId,
                identity.NormalizedAgentId,
                identity.InstanceId,
                identity.TokenHash,
                request.ExpectedVersion,
                now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(task => task.LeaseExpiresAt, expiresAt)
                .SetProperty(task => task.Version, task => task.Version + 1), cancellationToken);
        if (affected != 1)
        {
            throw await ResolveLeaseConflictAsync(
                taskId,
                request.ExpectedVersion,
                now,
                cancellationToken);
        }

        var renewed = await db.RemarkTasks.AsNoTracking()
            .SingleAsync(task => task.Id == taskId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToLeaseResponse(renewed, request.LeaseToken);
    }

    /// <summary>
    /// 在租约仍有效且持有证明匹配时主动释放任务，使其可立即被重新认领。
    /// </summary>
    /// <param name="taskId">待释放的任务标识。</param>
    /// <param name="agentId">路由提供的 Agent 标识。</param>
    /// <param name="request">租约持有证明和期望版本。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>释放后的待处理状态和版本。</returns>
    public async Task<RemarkTaskLeaseReleaseResponse> ReleaseAsync(
        Guid taskId,
        string agentId,
        RemarkTaskLeaseRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginWriteTransactionAsync(cancellationToken);
        var identity = await ValidateActiveLeaseRequestAsync(agentId, request.WeChatInstanceId, request.LeaseToken, cancellationToken);
        await synchronization.AfterBindingValidatedAsync("remark-task.release", cancellationToken);
        var now = timeProvider.GetUtcNow();
        var affected = await LeaseOwnerQuery(
                taskId,
                identity.NormalizedAgentId,
                identity.InstanceId,
                identity.TokenHash,
                request.ExpectedVersion,
                now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(task => task.ClaimedByAgentId, (string?)null)
                .SetProperty(task => task.ClaimedWeChatInstanceId, (string?)null)
                .SetProperty(task => task.LeaseTokenHash, (string?)null)
                .SetProperty(task => task.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(task => task.Version, task => task.Version + 1), cancellationToken);
        if (affected != 1)
        {
            throw await ResolveLeaseConflictAsync(
                taskId,
                request.ExpectedVersion,
                now,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new RemarkTaskLeaseReleaseResponse(
            taskId,
            RemarkTaskStatus.Pending,
            checked(request.ExpectedVersion + 1));
    }

    /// <summary>
    /// 在串行事务中验证租约和业务约束，并幂等写入成功或失败终态。
    /// </summary>
    /// <param name="taskId">待完成的任务标识。</param>
    /// <param name="agentId">路由提供的 Agent 标识。</param>
    /// <param name="request">租约持有证明、结果幂等标识和执行结果。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>稳定的完成响应；同一结果重试会标记为重放。</returns>
    public async Task<RemarkTaskLeaseCompletionResponse> CompleteAsync(
        Guid taskId,
        string agentId,
        RemarkTaskLeaseCompleteRequest request,
        CancellationToken cancellationToken)
    {
        // 结果字段先统一为空值或稳定文本，后续首次提交和幂等重放必须使用同一套比较语义。
        var normalizedPayload = NormalizeCompletionPayload(request);
        var normalizedResultId = request.ResultId.Trim();
        var instanceId = request.WeChatInstanceId.Trim();
        var normalizedAgentId = AgentControlService.NormalizeAgentId(agentId);
        var tokenHash = HashLeaseToken(request.LeaseToken);

        await using var transaction = await BeginWriteTransactionAsync(cancellationToken);
        try
        {
            db.ChangeTracker.Clear();
            // 写锁已在身份校验前取得，凭据轮换或吊销只能排在本次状态转换之后提交。
            await agents.EnsureActiveBindingAsync(agentId, instanceId, cancellationToken);
            await synchronization.AfterBindingValidatedAsync("remark-task.complete", cancellationToken);
            var replay = await TryResolveCompletionReplayAsync(
                taskId,
                normalizedResultId,
                normalizedPayload,
                cancellationToken);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }

            var task = await db.RemarkTasks.SingleOrDefaultAsync(x => x.Id == taskId, cancellationToken)
                       ?? throw DomainException.NotFound("Remark task");
            var now = timeProvider.GetUtcNow();

            // 首次重放查询与写锁之间可能已有并发请求提交终态，必须在锁内再次识别完全相同的结果。
            var concurrentReplay = TryCreateCompletionReplay(
                task,
                taskId,
                normalizedResultId,
                normalizedPayload);
            if (concurrentReplay is not null)
            {
                // 当前事务只执行过无副作用的加锁语句，显式回滚后返回已提交请求的稳定结果。
                await transaction.RollbackAsync(cancellationToken);
                return concurrentReplay;
            }

            if (task.Status != RemarkTaskStatus.Pending)
            {
                throw DomainException.Conflict(
                    "remark_task_not_pending",
                    "Only pending remark tasks can be completed by an Agent.");
            }
            if (task.Version != request.ExpectedVersion)
            {
                throw DomainException.Conflict(
                    "concurrency_conflict",
                    "The remark task changed after it was read.");
            }
            if (task.LeaseExpiresAt is null || task.LeaseExpiresAt <= now)
            {
                throw DomainException.Conflict(
                    "remark_task_lease_expired",
                    "The remark task lease has expired and cannot be used.");
            }
            if (!string.Equals(task.ClaimedByAgentId, normalizedAgentId, StringComparison.Ordinal) ||
                !string.Equals(task.ClaimedWeChatInstanceId, instanceId, StringComparison.Ordinal) ||
                !FixedTimeHashEquals(task.LeaseTokenHash, tokenHash))
            {
                throw DomainException.Conflict(
                    "remark_task_lease_not_owned",
                    "The remark task lease is not owned by this Agent binding or the token is invalid.");
            }
            ValidateCompletionPayload(task, normalizedPayload);

            if (normalizedPayload.Succeeded)
            {
                await remarks.ApplySuccessfulTaskAsync(
                    task,
                    normalizedPayload.AppliedRemark,
                    now,
                    cancellationToken);
                task.Status = RemarkTaskStatus.Completed;
                task.FailureReason = null;
            }
            else
            {
                task.Status = RemarkTaskStatus.Failed;
                task.FailureReason = normalizedPayload.FailureReason;
            }

            // 进入终态后清除全部活动租约字段，旧令牌即使泄露也无法继续操作。
            task.CompletionResultId = normalizedResultId;
            task.CompletedAt = now;
            task.ClaimedByAgentId = null;
            task.ClaimedWeChatInstanceId = null;
            task.LeaseTokenHash = null;
            task.LeaseExpiresAt = null;
            task.Version++;
            audit.Add(
                "remark-task.agent-completed",
                nameof(RemarkTask),
                task.Id.ToString("D"),
                normalizedPayload.Succeeded,
                new
                {
                    agentId = normalizedAgentId,
                    weChatInstanceId = instanceId,
                    resultIdHash = StableHash.Sha256(normalizedResultId),
                    task.Status,
                    task.AttemptCount
                });

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToCompletionResponse(task, normalizedResultId, false);
        }
        catch (DbUpdateException)
        {
            // 唯一结果 ID 竞争时只接受完全相同的已提交结果，否则保持冲突失败。
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var replay = await TryResolveCompletionReplayAsync(
                taskId,
                normalizedResultId,
                normalizedPayload,
                cancellationToken);
            if (replay is not null) return replay;
            throw;
        }
    }

    /// <summary>
    /// 在单一写事务内预览并创建管理员备注任务，保证暂停和权益门禁不能被并发状态写入穿透。
    /// </summary>
    /// <param name="request">规则和目标标识。</param>
    /// <param name="idempotencyKey">已校验的请求幂等键。</param>
    /// <param name="requestHash">规则和目标组成的稳定请求摘要。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>新建或幂等重放的备注任务。</returns>
    public async Task<AdministrativeRemarkTaskCreationResult> CreateAdministrativelyAsync(
        RemarkTaskRequest request,
        string idempotencyKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginWriteTransactionAsync(cancellationToken);
        try
        {
            db.ChangeTracker.Clear();
            var existing = await db.RemarkTasks.AsNoTracking()
                .SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new AdministrativeRemarkTaskCreationResult(
                    ValidateAdministrativeReplay(existing, requestHash),
                    true);
            }

            var preview = await remarks.PreviewAsync(request.RuleId, request.TargetId, cancellationToken);
            var automationPaused = await db.Tenants.AsNoTracking()
                .Select(x => x.AutomationPaused)
                .SingleAsync(cancellationToken);
            if (automationPaused)
            {
                throw DomainException.Conflict(
                    "automation_paused",
                    "Automation is paused; new remark tasks cannot be created.");
            }
            try
            {
                await EnsureAutoRemarkEntitledForCreationAsync(
                    preview.TargetKind,
                    request.TargetId,
                    cancellationToken);
            }
            catch (DomainException exception) when (exception.Code == "auto_remark_feature_required")
            {
                throw;
            }
            await remarkTaskSynchronization.AfterStateValidatedAsync(
                "remark-task.admin-create",
                cancellationToken);

            var now = timeProvider.GetUtcNow();
            var task = new RemarkTask
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId,
                RuleId = request.RuleId,
                TargetKind = preview.TargetKind,
                TargetId = request.TargetId,
                TargetExternalId = preview.TargetExternalId,
                ExpectedTargetDisplayName = preview.ExpectedTargetDisplayName,
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
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AdministrativeRemarkTaskCreationResult(task, false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            var existing = await db.RemarkTasks.AsNoTracking()
                .SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existing is null) throw;
            return new AdministrativeRemarkTaskCreationResult(
                ValidateAdministrativeReplay(existing, requestHash),
                true);
        }
        catch (DomainException exception) when (exception.Code is "automation_paused" or "auto_remark_feature_required")
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            await WriteAdministrativeCreationRejectionAuditAsync(
                exception.Code == "automation_paused"
                    ? "remark-task.rejected.automation-paused"
                    : "remark-task.rejected.feature-required",
                request.RuleId,
                request.TargetId,
                idempotencyKey,
                cancellationToken);
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>在当前写事务内保存管理员创建任务被门禁拒绝的审计记录。</summary>
    private async Task WriteAdministrativeCreationRejectionAuditAsync(
        string action,
        Guid ruleId,
        Guid targetId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        audit.Add(
            action,
            nameof(RemarkTask),
            targetId.ToString("D"),
            false,
            new
            {
                ruleId,
                targetId,
                idempotencyKeyHash = StableHash.Sha256(idempotencyKey)
            });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>确认目标当前具备自动备注权益。</summary>
    private async Task EnsureAutoRemarkEntitledForCreationAsync(
        ServiceTargetKind targetKind,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var entitlement = await remarks.FindAutoRemarkEntitlementAsync(
            targetKind,
            targetId,
            cancellationToken);
        if (entitlement is null)
        {
            throw DomainException.Conflict(
                "auto_remark_feature_required",
                "The target has no active entitlement granting auto-remark.");
        }
    }

    /// <summary>校验已有任务是否属于完全相同的管理员创建请求。</summary>
    private static RemarkTask ValidateAdministrativeReplay(RemarkTask task, string requestHash)
    {
        if (!string.Equals(task.RequestHash, requestHash, StringComparison.Ordinal))
        {
            throw DomainException.Conflict(
                "idempotency_key_reused",
                "The Idempotency-Key was already used for a different request.");
        }
        return task;
    }

    /// <summary>
    /// 在写事务中完成管理员接管，确保活动租约校验、目标备注更新、任务终态和审计原子提交。
    /// </summary>
    /// <param name="taskId">待完成的任务标识。</param>
    /// <param name="request">管理员提交的期望版本和执行结果。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>写入终态并清除过期租约后的任务。</returns>
    public async Task<RemarkTask> CompleteAdministrativelyAsync(
        Guid taskId,
        RemarkTaskCompleteRequest request,
        CancellationToken cancellationToken)
    {
        ValidateAdministrativeCompletionPayload(request);
        RemarkTask? task = null;
        DomainException? rejection = null;
        await using (var transaction = await BeginWriteTransactionAsync(cancellationToken))
        {
            try
            {
                db.ChangeTracker.Clear();
                task = await db.RemarkTasks.SingleOrDefaultAsync(x => x.Id == taskId, cancellationToken)
                       ?? throw DomainException.NotFound("Remark task");
                var now = timeProvider.GetUtcNow();

                if (task.Version != request.ExpectedVersion)
                {
                    throw DomainException.Conflict(
                        "concurrency_conflict",
                        "The remark task changed after it was read.");
                }
                if (task.Status != RemarkTaskStatus.Pending)
                {
                    throw DomainException.Conflict(
                        "remark_task_not_pending",
                        "Only pending remark tasks can be completed.");
                }
                if (task.LeaseExpiresAt is not null && task.LeaseExpiresAt > now)
                {
                    throw DomainException.Conflict(
                        "remark_task_leased",
                        "The remark task has an active Agent lease and cannot be completed through the administrative endpoint.");
                }

                await remarkTaskSynchronization.AfterStateValidatedAsync(
                    "remark-task.admin-complete",
                    cancellationToken);

                if (request.Succeeded)
                {
                    await remarks.ApplySuccessfulTaskAsync(
                        task,
                        request.AppliedRemark,
                        now,
                        cancellationToken);
                    task.Status = RemarkTaskStatus.Completed;
                    task.FailureReason = null;
                }
                else
                {
                    task.Status = RemarkTaskStatus.Failed;
                    task.FailureReason = request.FailureReason!.Trim();
                }

                task.CompletedAt = now;
                task.ClaimedByAgentId = null;
                task.ClaimedWeChatInstanceId = null;
                task.LeaseTokenHash = null;
                task.LeaseExpiresAt = null;
                task.Version++;
                audit.Add(
                    "remark-task.completed",
                    nameof(RemarkTask),
                    task.Id.ToString("D"),
                    request.Succeeded,
                    new
                    {
                        task.Status,
                        task.TargetKind,
                        task.TargetId,
                        task.FailureReason
                    });

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DomainException exception) when (IsAuditedAdministrativeRejection(exception.Code))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                rejection = exception;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        if (rejection is not null)
        {
            db.ChangeTracker.Clear();
            audit.Add(
                AdministrativeRejectionAuditAction(rejection.Code),
                nameof(RemarkTask),
                task!.Id.ToString("D"),
                false,
                new
                {
                    task.TargetKind,
                    task.TargetId,
                    errorCode = rejection.Code
                });
            await db.SaveChangesAsync(cancellationToken);
            throw rejection;
        }

        return task!;
    }

    /// <summary>判断业务拒绝是否需要沿用管理员完成接口的审计记录。</summary>
    private static bool IsAuditedAdministrativeRejection(string code) =>
        code is
            "automation_paused" or
            "auto_remark_feature_required" or
            "remark_now_protected" or
            "remark_target_identity_changed" or
            "remark_target_changed";

    /// <summary>把管理员完成业务拒绝映射为稳定审计动作。</summary>
    private static string AdministrativeRejectionAuditAction(string code) =>
        code switch
        {
            "automation_paused" => "remark-task.completion-rejected.automation-paused",
            "auto_remark_feature_required" => "remark-task.completion-rejected.feature-required",
            "remark_target_changed" or "remark_target_identity_changed" => "remark-task.rejected.target-changed",
            _ => "remark-task.completion-rejected.target-protected"
        };

    /// <summary>校验管理员完成载荷中的成功和失败结果字段必须严格互斥。</summary>
    private static void ValidateAdministrativeCompletionPayload(RemarkTaskCompleteRequest request)
    {
        if (request.Succeeded)
        {
            if (!string.IsNullOrWhiteSpace(request.FailureReason))
            {
                throw DomainException.Validation(
                    "failure_reason_not_allowed",
                    "FailureReason must be empty when the task succeeded.");
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(request.AppliedRemark))
        {
            throw DomainException.Validation(
                "applied_remark_not_allowed",
                "AppliedRemark must be empty when the task failed.");
        }
        if (string.IsNullOrWhiteSpace(request.FailureReason))
        {
            throw DomainException.Validation(
                "failure_reason_required",
                "FailureReason is required when the task failed.");
        }
    }

    /// <summary>
    /// 开启本次状态转换的数据库写事务，并执行零行更新取得 SQLite 写锁。
    /// 写锁必须早于 Agent 身份或管理员租约复核，才能消除“复核后并发写入”的时间窗口。
    /// </summary>
    /// <param name="cancellationToken">数据库事务取消令牌。</param>
    /// <returns>持有任务表写锁的 EF Core 事务。</returns>
    private async Task<IDbContextTransaction> BeginWriteTransactionAsync(CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE RemarkTasks SET Version = Version WHERE 0 = 1;",
                cancellationToken);
            return transaction;
        }
        catch
        {
            // 事务已经创建但写锁探针失败时，调用方拿不到事务对象，必须在此处主动回滚并释放连接。
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

    /// <summary>
    /// 创建包含全部显式持有条件的租约更新查询，不能只依赖 EF 全局过滤器或并发令牌元数据。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="normalizedAgentId">规范化 Agent 标识。</param>
    /// <param name="instanceId">微信实例标识。</param>
    /// <param name="tokenHash">租约令牌摘要。</param>
    /// <param name="expectedVersion">调用方期望版本。</param>
    /// <param name="now">判断租约是否仍有效的服务端时刻。</param>
    /// <returns>仅可能匹配当前有效持有者的查询。</returns>
    private IQueryable<RemarkTask> LeaseOwnerQuery(
        Guid taskId,
        string normalizedAgentId,
        string instanceId,
        string tokenHash,
        long expectedVersion,
        DateTimeOffset now) =>
        db.RemarkTasks.Where(task =>
            task.TenantId == tenant.TenantId &&
            task.Id == taskId &&
            task.Status == RemarkTaskStatus.Pending &&
            task.ClaimedByAgentId == normalizedAgentId &&
            task.ClaimedWeChatInstanceId == instanceId &&
            task.LeaseTokenHash == tokenHash &&
            task.LeaseExpiresAt != null &&
            task.LeaseExpiresAt > now &&
            task.Version == expectedVersion);

    /// <summary>
    /// 校验 Agent 的在线 dry-run 绑定，并生成租约匹配需要的规范化身份和令牌摘要。
    /// </summary>
    /// <param name="agentId">调用方 Agent 标识。</param>
    /// <param name="weChatInstanceId">调用方微信实例标识。</param>
    /// <param name="leaseToken">调用方持有的明文租约令牌。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>仅用于本次数据库条件匹配的规范化值。</returns>
    private async Task<LeaseIdentity> ValidateActiveLeaseRequestAsync(
        string agentId,
        string weChatInstanceId,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        var instanceId = weChatInstanceId.Trim();
        await agents.EnsureActiveBindingAsync(agentId, instanceId, cancellationToken);
        return new LeaseIdentity(
            AgentControlService.NormalizeAgentId(agentId),
            instanceId,
            HashLeaseToken(leaseToken));
    }

    /// <summary>
    /// 将零行原子更新转换为稳定的不存在、版本、过期或非持有者错误，不泄露令牌摘要。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="expectedVersion">请求期望版本。</param>
    /// <param name="now">更新时使用的服务端时刻。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>应向调用方抛出的领域异常。</returns>
    private async Task<DomainException> ResolveLeaseConflictAsync(
        Guid taskId,
        long expectedVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var task = await db.RemarkTasks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == taskId, cancellationToken);
        if (task is null) return DomainException.NotFound("Remark task");
        if (task.Status != RemarkTaskStatus.Pending)
        {
            return DomainException.Conflict(
                "remark_task_not_pending",
                "Only pending remark tasks can have an active lease.");
        }
        if (task.Version != expectedVersion)
        {
            return DomainException.Conflict(
                "concurrency_conflict",
                "The remark task changed after it was read.");
        }
        if (task.LeaseExpiresAt is null || task.LeaseExpiresAt <= now)
        {
            return DomainException.Conflict(
                "remark_task_lease_expired",
                "The remark task lease has expired and cannot be used.");
        }
        return DomainException.Conflict(
            "remark_task_lease_not_owned",
            "The remark task lease is not owned by this Agent binding or the token is invalid.");
    }

    /// <summary>
    /// 将调用方结果规范化为空值或稳定文本，使首次提交、事务内竞争检查和后续重放共享完全相同的比较语义。
    /// </summary>
    /// <param name="request">尚未规范化的完成请求。</param>
    /// <returns>保留成功备注精确值、裁剪失败原因并折叠空白可选字段后的结果载荷。</returns>
    private static CompletionPayload NormalizeCompletionPayload(RemarkTaskLeaseCompleteRequest request) =>
        new(
            request.Succeeded,
            string.IsNullOrWhiteSpace(request.AppliedRemark) ? null : request.AppliedRemark,
            string.IsNullOrWhiteSpace(request.FailureReason) ? null : request.FailureReason.Trim());

    /// <summary>
    /// 校验规范化结果的成功与失败字段互斥约束，防止首次提交的持久化结果存在多种解释。
    /// </summary>
    /// <param name="task">提供预期生成备注的待完成任务。</param>
    /// <param name="payload">已经按幂等协议规范化的完整结果载荷。</param>
    private static void ValidateCompletionPayload(
        RemarkTask task,
        CompletionPayload payload)
    {
        if (payload.Succeeded)
        {
            if (!string.Equals(payload.AppliedRemark, task.GeneratedRemark, StringComparison.Ordinal))
            {
                throw DomainException.Validation(
                    "applied_remark_mismatch",
                    "AppliedRemark must exactly match the generated remark.");
            }
            if (payload.FailureReason is not null)
            {
                throw DomainException.Validation(
                    "failure_reason_not_allowed",
                    "FailureReason must be empty when the task succeeded.");
            }
            return;
        }

        if (payload.FailureReason is null)
        {
            throw DomainException.Validation(
                "failure_reason_required",
                "FailureReason is required when the task failed.");
        }
        if (payload.AppliedRemark is not null)
        {
            throw DomainException.Validation(
                "applied_remark_not_allowed",
                "AppliedRemark must be empty when the task failed.");
        }
    }

    /// <summary>
    /// 尝试解析已提交的同一结果重试，并拒绝同一结果 ID 或任务绑定到不同结果载荷。
    /// </summary>
    /// <param name="taskId">本次完成路由指定的任务标识。</param>
    /// <param name="resultId">已裁剪且用于租户内唯一查找的结果标识。</param>
    /// <param name="payload">本次请求的规范化完整结果载荷。</param>
    /// <param name="cancellationToken">数据库查询的取消令牌。</param>
    /// <returns>尚无终态结果时返回空值，完整匹配时返回重放响应，发现绑定冲突时抛出领域异常。</returns>
    private async Task<RemarkTaskLeaseCompletionResponse?> TryResolveCompletionReplayAsync(
        Guid taskId,
        string resultId,
        CompletionPayload payload,
        CancellationToken cancellationToken)
    {
        var task = await db.RemarkTasks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompletionResultId == resultId, cancellationToken);
        if (task is null)
        {
            var requestedTask = await db.RemarkTasks.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == taskId, cancellationToken);
            if (requestedTask?.CompletionResultId is not null)
            {
                // 两次查询之间可能正好有相同请求提交完成；必须对第二次读取的终态执行完整重放比较，
                // 不能仅因第一次按结果标识未命中就误判为不同结果。若标识或载荷确有差异，统一比较仍会返回冲突。
                return TryCreateCompletionReplay(
                    requestedTask,
                    taskId,
                    resultId,
                    payload);
            }
            return null;
        }

        return TryCreateCompletionReplay(task, taskId, resultId, payload);
    }

    /// <summary>
    /// 对已经进入终态的任务严格比较任务绑定及规范化的三项结果字段，并创建稳定的幂等重放响应。
    /// </summary>
    /// <param name="task">数据库中读取到的请求任务或结果标识所属任务。</param>
    /// <param name="requestedTaskId">本次路由指定的任务标识。</param>
    /// <param name="resultId">已裁剪的调用方结果幂等标识。</param>
    /// <param name="payload">本次请求的规范化完整结果载荷。</param>
    /// <returns>结果尚未提交时返回空值；完全匹配时返回重放响应；绑定或载荷冲突时抛出领域冲突。</returns>
    private static RemarkTaskLeaseCompletionResponse? TryCreateCompletionReplay(
        RemarkTask task,
        Guid requestedTaskId,
        string resultId,
        CompletionPayload payload)
    {
        if (task.CompletionResultId is null) return null;

        // 完成状态是服务端持久化的成功标志；除完成和失败外的状态都不能作为合法重放。
        var persistedSucceeded = task.Status == RemarkTaskStatus.Completed;
        // 成功路径已强制应用生成备注，因此可从不可变任务生成值还原规范化的 AppliedRemark。
        var persistedAppliedRemark = persistedSucceeded ? task.GeneratedRemark : null;
        // 失败路径只持久化裁剪后的失败原因，成功路径的规范值始终为空。
        var persistedFailureReason = task.Status == RemarkTaskStatus.Failed ? task.FailureReason : null;
        // 三项结果字段、任务标识和结果标识必须全部一致，才属于同一请求的安全重放。
        var samePayload = task.Id == requestedTaskId &&
                          string.Equals(task.CompletionResultId, resultId, StringComparison.Ordinal) &&
                          task.Status is RemarkTaskStatus.Completed or RemarkTaskStatus.Failed &&
                          persistedSucceeded == payload.Succeeded &&
                          string.Equals(persistedAppliedRemark, payload.AppliedRemark, StringComparison.Ordinal) &&
                          string.Equals(persistedFailureReason, payload.FailureReason, StringComparison.Ordinal);
        if (!samePayload)
        {
            throw DomainException.Conflict(
                "remark_task_result_conflict",
                "The result identity was already used for a different remark task result.");
        }

        return ToCompletionResponse(task, resultId, true);
    }

    /// <summary>创建不可预测且适合 HTTP JSON 传输的 Base64Url 租约令牌。</summary>
    private static string CreateLeaseToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(LeaseTokenBytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>计算租约令牌的固定长度 SHA-256 摘要，数据库只保存该值。</summary>
    private static string HashLeaseToken(string leaseToken) => StableHash.Sha256(leaseToken);

    /// <summary>以固定时间比较十六进制摘要，畸形数据库值按不匹配处理。</summary>
    private static bool FixedTimeHashEquals(string? storedHash, string suppliedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash)) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(storedHash),
                Convert.FromHexString(suppliedHash));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>将数据库任务转换为只向当前持有者返回的租约响应。</summary>
    /// <param name="task">已成功持有租约的任务。</param>
    /// <param name="leaseToken">仅返回给持有者的明文租约令牌。</param>
    /// <returns>包含创建时目标身份快照的租约响应。</returns>
    private static RemarkTaskLeaseResponse ToLeaseResponse(
        RemarkTask task,
        string leaseToken) => new(
            task.Id,
            task.TargetKind,
            task.TargetId,
            task.TargetExternalId,
            task.GeneratedRemark,
            task.ExpectedTargetDisplayName,
            task.OriginalWeChatRemark,
            leaseToken,
            task.LeaseExpiresAt ?? throw new InvalidOperationException("The claimed task has no lease expiry."),
            task.AttemptCount,
            task.Version);

    /// <summary>将终态任务转换为稳定的完成响应。</summary>
    private static RemarkTaskLeaseCompletionResponse ToCompletionResponse(
        RemarkTask task,
        string resultId,
        bool replayed) => new(
        task.Id,
        task.Status,
        resultId,
        task.CompletedAt ?? throw new InvalidOperationException("The completed task has no completion timestamp."),
        task.Version,
        replayed);

    /// <summary>在处理请求前验证租约配置范围，防止错误配置导致永久独占或续租风暴。</summary>
    private void ValidateConfiguration()
    {
        if (options.Value.DurationSeconds is < MinimumLeaseSeconds or > MaximumLeaseSeconds)
        {
            throw new InvalidOperationException(
                $"RemarkTaskLease__DurationSeconds must be between {MinimumLeaseSeconds} and {MaximumLeaseSeconds}.");
        }
    }

    /// <summary>保存一次租约请求所需的规范化身份和令牌摘要。</summary>
    /// <param name="NormalizedAgentId">规范化 Agent 标识。</param>
    /// <param name="InstanceId">已去除首尾空白的微信实例标识。</param>
    /// <param name="TokenHash">租约令牌的 SHA-256 摘要。</param>
    private sealed record LeaseIdentity(
        string NormalizedAgentId,
        string InstanceId,
        string TokenHash);

    /// <summary>保存完成协议用于首次校验和幂等比较的规范化完整结果。</summary>
    /// <param name="Succeeded">表示外部备注操作是否被调用方确认成功。</param>
    /// <param name="AppliedRemark">成功时精确匹配生成值的实际备注；失败时必须为空。</param>
    /// <param name="FailureReason">失败时经裁剪的稳定原因；成功时必须为空。</param>
    private sealed record CompletionPayload(
        bool Succeeded,
        string? AppliedRemark,
        string? FailureReason);
}
