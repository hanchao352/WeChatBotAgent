using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;

namespace WeChatBot.Backend.Services;

/// <summary>
/// 管理 Agent 注册与凭据生命周期、验证心跳身份，并维护租约判断所需的权威在线状态。
/// </summary>
public sealed class AgentControlService(
    AppDbContext db,
    TenantContext tenant,
    TimeProvider timeProvider,
    AuditService audit,
    IOptions<AuthOptions> authOptions,
    AgentIdentityBindingService identityBinding)
{
    /// <summary>允许 Agent 时钟相对服务端时间偏移的最大范围。</summary>
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>心跳从 Agent 发出到服务端接收允许的最长年龄。</summary>
    private static readonly TimeSpan MaximumHeartbeatAge = TimeSpan.FromMinutes(2);

    /// <summary>判断 Agent 是否在线的服务端接收时间窗口。</summary>
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(60);

    /// <summary>同一错误实例绑定拒绝事件的最短审计间隔，避免异常请求淹没日志。</summary>
    private static readonly TimeSpan RejectionAuditInterval = TimeSpan.FromMinutes(5);

    /// <summary>心跳状态遇到乐观并发竞争时允许的有限更新尝试次数。</summary>
    private const int HeartbeatUpdateAttempts = 8;

    /// <summary>生成凭据时用于处理理论摘要碰撞的有限尝试次数。</summary>
    private const int CredentialGenerationAttempts = 4;

    /// <summary>验证认证身份与心跳正文绑定，并按时间顺序更新权威运行状态。</summary>
    /// <param name="request">包含设备身份、时间戳、运行状态和 dry-run 状态的心跳。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>控制面是否接受当前会话以及急停和配置版本。</returns>
    public async Task<AgentHeartbeatResponse> RecordHeartbeatAsync(
        AgentHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        ValidateTimestamps(request, now);

        var agentId = request.AgentId.Trim();
        var normalizedAgentId = NormalizeAgentId(agentId);
        var weChatInstanceId = request.WeChatInstanceId.Trim();
        AgentRegistration registration;
        if (identityBinding.IsLegacySharedCredential)
        {
            // 只有显式兼容身份允许先按旧协议自动注册；独立凭据绝不能借请求正文创建任意身份。
            registration = await EnsureRegistrationAsync(
                agentId,
                normalizedAgentId,
                weChatInstanceId,
                now,
                cancellationToken);
            _ = await identityBinding.RequireHeartbeatAsync(
                agentId,
                weChatInstanceId,
                cancellationToken);
        }
        else
        {
            // 独立凭据先验证 claim 与正文绑定，再按服务端主键读取注册，正文不能选择数据库身份。
            var identity = await identityBinding.RequireHeartbeatAsync(
                agentId,
                weChatInstanceId,
                cancellationToken);
            registration = await db.AgentRegistrations
                .SingleAsync(x => x.Id == identity.RegistrationId, cancellationToken);
        }

        var emergencyStop = await db.Tenants.AsNoTracking()
            .Select(x => x.AutomationPaused)
            .SingleAsync(cancellationToken);
        var identityMatches = string.Equals(
            registration.WeChatInstanceId,
            weChatInstanceId,
            StringComparison.Ordinal);

        if (!identityMatches)
        {
            var state = await db.AgentHeartbeatStates
                .SingleOrDefaultAsync(x => x.AgentRegistrationId == registration.Id, cancellationToken);
            var auditRejection = state?.LastRejectedAt is null ||
                                 now - state.LastRejectedAt.Value >= RejectionAuditInterval;
            if (auditRejection)
            {
                if (state is not null)
                {
                    state.LastRejectedAt = now;
                    state.LastRejectedWeChatInstanceId = weChatInstanceId;
                    state.Version++;
                }
                audit.Add(
                    "agent.heartbeat.binding_rejected",
                    nameof(AgentRegistration),
                    registration.Id.ToString("D"),
                    false,
                    new
                    {
                        registration.AgentId,
                        registeredWeChatInstanceId = registration.WeChatInstanceId,
                        rejectedWeChatInstanceId = weChatInstanceId
                    });
                await db.SaveChangesAsync(cancellationToken);
            }
            return new AgentHeartbeatResponse(false, emergencyStop, registration.ConfigurationVersion);
        }

        var authoritativeDryRun = await UpsertHeartbeatStateAsync(
            registration.Id,
            request,
            now,
            cancellationToken);
        return new AgentHeartbeatResponse(
            registration.IsEnabled && request.DryRun && authoritativeDryRun,
            emergencyStop,
            registration.ConfigurationVersion);
    }

    /// <summary>验证 Agent 当前凭据绑定、自动化开关和健康心跳均满足业务租约要求。</summary>
    /// <param name="agentId">路由声明的 Agent 标识。</param>
    /// <param name="weChatInstanceId">正文声明的微信实例标识。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    public async Task EnsureActiveBindingAsync(
        string agentId,
        string weChatInstanceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agentId) || agentId.Length > 128 ||
            string.IsNullOrWhiteSpace(weChatInstanceId) || weChatInstanceId.Length > 128)
        {
            throw DomainException.Validation(
                "invalid_agent_binding",
                "AgentId and WeChatInstanceId are required and must be at most 128 characters.");
        }

        // 认证身份、请求身份和数据库注册必须先完全一致，随后才检查瞬时在线状态。
        var identity = await identityBinding.RequireAsync(
            agentId,
            weChatInstanceId,
            cancellationToken);
        var onlineAfter = timeProvider.GetUtcNow() - OnlineWindow;
        var controlState = await db.Tenants.AsNoTracking()
            .Select(x => new { x.AutomationPaused, x.UpdatedAt })
            .SingleAsync(cancellationToken);
        if (controlState.AutomationPaused)
        {
            throw DomainException.Conflict(
                "automation_paused",
                "Automation is paused; Agent operations are unavailable.");
        }

        var registration = await db.AgentRegistrations.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == identity.RegistrationId,
                cancellationToken);
        // 即使心跳状态仍在在线窗口内，轮换、吊销或恢复清除摘要后也不得继续使用旧业务租约。
        var credentialUnavailable = registration?.CredentialHash is null &&
                                    !identityBinding.IsLegacySharedCredential;
        // 兼容身份可能由旧共享 Key 声明错误实例，必须由业务绑定层继续拒绝而不能复用正确实例的心跳。
        var instanceMismatch = registration is not null &&
                               (!string.Equals(
                                    registration.WeChatInstanceId,
                                    identity.WeChatInstanceId,
                                    StringComparison.Ordinal) ||
                                !string.Equals(
                                    registration.WeChatInstanceId,
                                    weChatInstanceId.Trim(),
                                    StringComparison.Ordinal));
        if (registration is null ||
            !registration.IsEnabled ||
            credentialUnavailable ||
            registration.CredentialRevokedAt is not null ||
            instanceMismatch)
        {
            throw DomainException.Conflict(
                "agent_lease_unavailable",
                "The active Agent binding could not be verified.");
        }

        var state = await db.AgentHeartbeatStates.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.AgentRegistrationId == registration.Id,
                cancellationToken);
        var credentialSessionStartedAt = GetCredentialSessionStartedAt(registration);
        if (state is null ||
            state.ReceivedAt < onlineAfter ||
            state.SentAt < onlineAfter ||
            state.ReceivedAt < controlState.UpdatedAt ||
            state.ReceivedAt < credentialSessionStartedAt ||
            state.RuntimeState != AgentOperatingState.Healthy ||
            !state.DryRun)
        {
            throw DomainException.Conflict(
                "agent_lease_unavailable",
                "The active Agent binding could not be verified.");
        }
    }

    /// <summary>列出当前租户的注册、凭据生命周期和瞬时在线状态，不返回任何凭据材料。</summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>按 AgentId 稳定排序的安全注册视图。</returns>
    public async Task<IReadOnlyList<AgentListItem>> ListAsync(CancellationToken cancellationToken)
    {
        var registrations = await db.AgentRegistrations.AsNoTracking()
            .OrderBy(x => x.AgentId)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var states = await db.AgentHeartbeatStates.AsNoTracking()
            .ToDictionaryAsync(x => x.AgentRegistrationId, cancellationToken);
        var onlineAfter = timeProvider.GetUtcNow() - OnlineWindow;

        return registrations.Select(registration =>
        {
            states.TryGetValue(registration.Id, out var state);
            var credentialSessionStartedAt = GetCredentialSessionStartedAt(registration);
            return new AgentListItem(
                registration.Id,
                registration.AgentId,
                registration.WeChatInstanceId,
                registration.IsEnabled,
                registration.ConfigurationVersion,
                registration.RegisteredAt,
                registration.UpdatedAt,
                registration.Version,
                state?.SentAt,
                state?.ReceivedAt,
                state?.RuntimeState,
                state?.ReasonCode,
                state?.Reason,
                state?.ChangedAt,
                state?.LastCommandCompletedAt,
                state?.LastCommandCode,
                state?.QueueDepth,
                state?.ActiveExecutions,
                state?.DryRun,
                state?.AgentVersion,
                registration.IsEnabled &&
                registration.CredentialHash is not null &&
                registration.CredentialRevokedAt is null &&
                state?.ReceivedAt >= onlineAfter &&
                state.ReceivedAt >= credentialSessionStartedAt,
                registration.CredentialHash is not null && registration.CredentialRevokedAt is null,
                registration.CredentialIssuedAt,
                registration.CredentialRotatedAt,
                registration.CredentialRevokedAt);
        }).ToList();
    }

    /// <summary>
    /// 预注册 Agent 并首次签发只返回一次的独立凭据；重复注册一律冲突，服务端无法也不会恢复旧明文。
    /// </summary>
    /// <param name="request">不可变 AgentId、微信实例和配置版本。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>无敏感摘要的注册视图和仅本次响应可见的明文凭据。</returns>
    public async Task<AgentCredentialIssueResponse> RegisterAsync(
        RegisterAgentRequest request,
        CancellationToken cancellationToken)
    {
        var agentId = request.AgentId.Trim();
        var normalizedAgentId = NormalizeAgentId(agentId);
        var weChatInstanceId = request.WeChatInstanceId.Trim();
        var configurationVersion = request.ConfigurationVersion.Trim();
        var existing = await db.AgentRegistrations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.NormalizedAgentId == normalizedAgentId, cancellationToken);
        if (existing is not null)
        {
            // 即使不可变字段完全相同，也不能重放首次响应或伪造一个无法验证的新明文凭据。
            throw DomainException.Conflict(
                "agent_already_registered",
                "The AgentId is already registered; rotate its credential instead of repeating registration.");
        }
        var existingInstance = await db.AgentRegistrations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WeChatInstanceId == weChatInstanceId, cancellationToken);
        if (existingInstance is not null)
        {
            throw DomainException.Conflict(
                "wechat_instance_already_registered",
                "The WeChatInstanceId is already registered to another AgentId.");
        }

        var now = timeProvider.GetUtcNow();
        var credential = await CreateUnusedCredentialAsync(cancellationToken);
        var registration = new AgentRegistration
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            AgentId = agentId,
            NormalizedAgentId = normalizedAgentId,
            WeChatInstanceId = weChatInstanceId,
            IsEnabled = true,
            ConfigurationVersion = configurationVersion,
            CredentialHash = AgentCredentialSecurity.HashCredential(credential),
            CredentialIssuedAt = now,
            RegisteredAt = now,
            UpdatedAt = now
        };
        db.AgentRegistrations.Add(registration);
        audit.Add(
            "agent.pre-registered",
            nameof(AgentRegistration),
            registration.Id.ToString("D"),
            details: new
            {
                registration.AgentId,
                registration.WeChatInstanceId,
                registration.ConfigurationVersion
            });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await db.AgentRegistrations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.NormalizedAgentId == normalizedAgentId, cancellationToken);
            if (existing is not null)
            {
                throw DomainException.Conflict(
                    "agent_already_registered",
                    "The AgentId was concurrently registered; rotate its credential if a new credential is required.");
            }
            existingInstance = await db.AgentRegistrations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.WeChatInstanceId == weChatInstanceId, cancellationToken);
            if (existingInstance is not null)
            {
                throw DomainException.Conflict(
                    "wechat_instance_already_registered",
                    "The WeChatInstanceId is already registered to another AgentId.");
            }
            throw;
        }
        return new AgentCredentialIssueResponse(
            ToListItem(registration, null, now - OnlineWindow),
            credential);
    }

    /// <summary>
    /// 原子轮换指定注册的独立凭据，使旧凭据在事务提交后立即失效并只返回一次新明文。
    /// </summary>
    /// <param name="registrationId">待轮换的 AgentRegistration 主键。</param>
    /// <param name="request">管理员读取到的期望并发版本。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>更新后注册视图和单次可见的新凭据。</returns>
    public async Task<AgentCredentialIssueResponse> RotateCredentialAsync(
        Guid registrationId,
        AgentCredentialVersionRequest request,
        CancellationToken cancellationToken)
    {
        var registration = await RequireRegistrationVersionAsync(
            registrationId,
            request.ExpectedVersion,
            cancellationToken);
        if (!registration.IsEnabled)
        {
            throw DomainException.Conflict(
                "agent_registration_disabled",
                "A disabled Agent registration cannot receive a new credential.");
        }

        var now = timeProvider.GetUtcNow();
        var credential = await CreateUnusedCredentialAsync(cancellationToken);
        registration.CredentialHash = AgentCredentialSecurity.HashCredential(credential);
        registration.CredentialIssuedAt ??= now;
        registration.CredentialRotatedAt = now;
        registration.CredentialRevokedAt = null;
        registration.UpdatedAt = now;
        registration.Version++;
        // 保留旧心跳作为运维遥测；在线和租约判断会按本次轮换时间建立新的凭据会话边界。
        audit.Add(
            "agent.credential-rotated",
            nameof(AgentRegistration),
            registration.Id.ToString("D"),
            details: new
            {
                registration.AgentId,
                registration.WeChatInstanceId,
                registration.Version
            });
        await db.SaveChangesAsync(cancellationToken);
        return new AgentCredentialIssueResponse(
            ToListItem(registration, null, now - OnlineWindow),
            credential);
    }

    /// <summary>
    /// 原子吊销指定注册的当前凭据并清除摘要，使旧明文无法再通过认证。
    /// </summary>
    /// <param name="registrationId">待吊销的 AgentRegistration 主键。</param>
    /// <param name="request">管理员读取到的期望并发版本。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>不含任何凭据材料的更新后注册视图。</returns>
    public async Task<AgentListItem> RevokeCredentialAsync(
        Guid registrationId,
        AgentCredentialVersionRequest request,
        CancellationToken cancellationToken)
    {
        var registration = await RequireRegistrationVersionAsync(
            registrationId,
            request.ExpectedVersion,
            cancellationToken);
        if (registration.CredentialHash is null && registration.CredentialRevokedAt is not null)
        {
            throw DomainException.Conflict(
                "agent_credential_already_revoked",
                "The Agent credential is already revoked.");
        }

        var now = timeProvider.GetUtcNow();
        // 清除摘要而不是保留可验证材料；吊销时间仍提供非敏感运维事实。
        registration.CredentialHash = null;
        registration.CredentialRevokedAt = now;
        registration.UpdatedAt = now;
        registration.Version++;
        // 保留最后一次心跳作为审计和故障诊断事实；凭据为空或已吊销时列表与租约门禁仍立即离线。
        audit.Add(
            "agent.credential-revoked",
            nameof(AgentRegistration),
            registration.Id.ToString("D"),
            details: new
            {
                registration.AgentId,
                registration.WeChatInstanceId,
                registration.Version
            });
        await db.SaveChangesAsync(cancellationToken);
        return ToListItem(registration, null, now - OnlineWindow);
    }

    /// <summary>去除首尾空白并按不依赖区域性的规则规范化 AgentId。</summary>
    /// <param name="agentId">注册或请求提供的 AgentId。</param>
    /// <returns>用于租户内唯一索引和比较的规范化值。</returns>
    public static string NormalizeAgentId(string agentId) => agentId.Trim().ToUpperInvariant();

    /// <summary>把注册和可选心跳投影为不含凭据材料的列表契约。</summary>
    /// <param name="registration">数据库注册事实。</param>
    /// <param name="state">可选瞬时心跳状态。</param>
    /// <param name="onlineAfter">仍可视为在线的最早接收时间。</param>
    /// <returns>管理员可安全读取的注册视图。</returns>
    private static AgentListItem ToListItem(
        AgentRegistration registration,
        AgentHeartbeatState? state,
        DateTimeOffset onlineAfter) => new(
        registration.Id,
        registration.AgentId,
        registration.WeChatInstanceId,
        registration.IsEnabled,
        registration.ConfigurationVersion,
        registration.RegisteredAt,
        registration.UpdatedAt,
        registration.Version,
        state?.SentAt,
        state?.ReceivedAt,
        state?.RuntimeState,
        state?.ReasonCode,
        state?.Reason,
        state?.ChangedAt,
        state?.LastCommandCompletedAt,
        state?.LastCommandCode,
        state?.QueueDepth,
        state?.ActiveExecutions,
        state?.DryRun,
        state?.AgentVersion,
        registration.IsEnabled && state?.ReceivedAt >= onlineAfter,
        registration.CredentialHash is not null && registration.CredentialRevokedAt is null,
        registration.CredentialIssuedAt,
        registration.CredentialRotatedAt,
        registration.CredentialRevokedAt);

    /// <summary>
    /// 计算当前凭据会话的服务端起点；只有该时刻之后接收的心跳才能证明新凭据已经重新上线。
    /// </summary>
    /// <param name="registration">包含首次签发和最近轮换时间的 Agent 注册。</param>
    /// <returns>最近轮换时间；尚未轮换时为首次签发时间；旧兼容注册无时间时使用最小值。</returns>
    private static DateTimeOffset GetCredentialSessionStartedAt(AgentRegistration registration) =>
        registration.CredentialRotatedAt ??
        registration.CredentialIssuedAt ??
        DateTimeOffset.MinValue;

    /// <summary>按主键读取注册并验证管理员携带的乐观并发版本。</summary>
    /// <param name="registrationId">AgentRegistration 主键。</param>
    /// <param name="expectedVersion">管理员期望的当前版本。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>可跟踪且版本匹配的注册实体。</returns>
    private async Task<AgentRegistration> RequireRegistrationVersionAsync(
        Guid registrationId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var registration = await db.AgentRegistrations
            .SingleOrDefaultAsync(x => x.Id == registrationId, cancellationToken)
            ?? throw DomainException.NotFound("Agent registration");
        if (registration.Version != expectedVersion)
        {
            throw DomainException.Conflict(
                "concurrency_conflict",
                "The Agent registration changed after it was read.");
        }
        return registration;
    }

    /// <summary>生成不与现有摘要、管理员密钥或兼容共享密钥冲突的高熵凭据。</summary>
    /// <param name="cancellationToken">数据库唯一性预检的取消令牌。</param>
    /// <returns>尚未持久化且只应返回一次的明文凭据。</returns>
    private async Task<string> CreateUnusedCredentialAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < CredentialGenerationAttempts; attempt++)
        {
            var credential = AgentCredentialSecurity.CreateCredential();
            var hash = AgentCredentialSecurity.HashCredential(credential);
            var collidesWithConfiguredRole = string.Equals(
                                                credential,
                                                authOptions.Value.ApiKey,
                                                StringComparison.Ordinal) ||
                                            string.Equals(
                                                credential,
                                                authOptions.Value.AgentApiKey,
                                                StringComparison.Ordinal);
            if (!collidesWithConfiguredRole &&
                !await db.AgentRegistrations.AsNoTracking()
                    .AnyAsync(x => x.CredentialHash == hash, cancellationToken))
            {
                return credential;
            }
        }

        throw new InvalidOperationException(
            "A unique Agent credential could not be generated after the configured safety attempts.");
    }

    /// <summary>仅为显式旧共享凭据兼容路径读取或按配置自动创建注册。</summary>
    /// <param name="agentId">调用方声明的 AgentId。</param>
    /// <param name="normalizedAgentId">规范化 AgentId。</param>
    /// <param name="weChatInstanceId">调用方声明的微信实例标识。</param>
    /// <param name="now">本次心跳统一服务端时间。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>已存在或兼容模式新建的注册。</returns>
    private async Task<AgentRegistration> EnsureRegistrationAsync(
        string agentId,
        string normalizedAgentId,
        string weChatInstanceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await db.AgentRegistrations
            .SingleOrDefaultAsync(x => x.NormalizedAgentId == normalizedAgentId, cancellationToken);
        if (existing is not null) return existing;
        if (!authOptions.Value.AllowAgentAutoRegistration)
        {
            throw DomainException.Conflict(
                "agent_not_registered",
                "The Agent must be pre-registered before it can establish a heartbeat lease.");
        }

        var registration = new AgentRegistration
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            AgentId = agentId,
            NormalizedAgentId = normalizedAgentId,
            WeChatInstanceId = weChatInstanceId,
            IsEnabled = true,
            ConfigurationVersion = "1",
            RegisteredAt = now,
            UpdatedAt = now
        };
        db.AgentRegistrations.Add(registration);
        audit.Add(
            "agent.registered",
            nameof(AgentRegistration),
            registration.Id.ToString("D"),
            details: new { registration.AgentId, registration.WeChatInstanceId });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return registration;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await db.AgentRegistrations
                .SingleOrDefaultAsync(x => x.NormalizedAgentId == normalizedAgentId, cancellationToken);
            if (existing is not null) return existing;
            var existingInstance = await db.AgentRegistrations
                .SingleOrDefaultAsync(x => x.WeChatInstanceId == weChatInstanceId, cancellationToken);
            if (existingInstance is not null)
            {
                throw DomainException.Conflict(
                    "wechat_instance_already_registered",
                    "The WeChatInstanceId is already registered to another AgentId.");
            }
            throw;
        }
    }

    /// <summary>以乐观并发重试按 Agent 发送时间更新心跳，拒绝旧消息覆盖新状态。</summary>
    /// <param name="registrationId">已验证注册主键。</param>
    /// <param name="request">本次心跳正文。</param>
    /// <param name="receivedAt">服务端接收时间。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>数据库中最终权威状态是否仍为 dry-run。</returns>
    private async Task<bool> UpsertHeartbeatStateAsync(
        Guid registrationId,
        AgentHeartbeatRequest request,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < HeartbeatUpdateAttempts; attempt++)
        {
            var state = await db.AgentHeartbeatStates
                .SingleOrDefaultAsync(x => x.AgentRegistrationId == registrationId, cancellationToken);
            if (state is not null && request.SentAt <= state.SentAt) return state.DryRun;

            if (state is null)
            {
                state = new AgentHeartbeatState
                {
                    AgentRegistrationId = registrationId,
                    TenantId = tenant.TenantId
                };
                db.AgentHeartbeatStates.Add(state);
            }

            ApplyHeartbeat(state, request, receivedAt);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return state.DryRun;
            }
            catch (DbUpdateException) when (attempt < HeartbeatUpdateAttempts - 1)
            {
                db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(5 * (attempt + 1)), cancellationToken);
            }
        }

        throw new InvalidOperationException("Agent heartbeat state could not be updated after concurrent retries.");
    }

    /// <summary>把已验证且不早于当前状态的心跳字段应用到跟踪实体。</summary>
    /// <param name="state">待更新的心跳实体。</param>
    /// <param name="request">本次心跳正文。</param>
    /// <param name="receivedAt">服务端接收时间。</param>
    private static void ApplyHeartbeat(
        AgentHeartbeatState state,
        AgentHeartbeatRequest request,
        DateTimeOffset receivedAt)
    {
        state.SentAt = request.SentAt;
        state.ReceivedAt = receivedAt;
        state.RuntimeState = request.Runtime.State;
        state.ReasonCode = request.Runtime.ReasonCode.Trim();
        state.Reason = request.Runtime.Reason.Trim();
        state.ChangedAt = request.Runtime.ChangedAt;
        state.LastCommandCompletedAt = request.Runtime.LastCommandCompletedAt;
        state.LastCommandCode = NullIfWhiteSpace(request.Runtime.LastCommandCode);
        state.QueueDepth = request.QueueDepth;
        state.ActiveExecutions = request.ActiveExecutions;
        state.DryRun = request.DryRun;
        state.AgentVersion = request.AgentVersion.Trim();
        state.Version++;
    }

    /// <summary>拒绝未来漂移过大、过旧或运行状态时间晚于发送时间的心跳。</summary>
    /// <param name="request">待验证心跳。</param>
    /// <param name="now">当前服务端时间。</param>
    private static void ValidateTimestamps(AgentHeartbeatRequest request, DateTimeOffset now)
    {
        if (request.SentAt == default || request.Runtime.ChangedAt == default)
            throw DomainException.Validation("invalid_heartbeat_timestamp", "Heartbeat timestamps are required.");
        if (request.SentAt > now + MaximumClockSkew)
            throw DomainException.Validation("heartbeat_clock_skew", "Heartbeat SentAt is too far in the future.");
        if (request.SentAt < now - MaximumHeartbeatAge)
            throw DomainException.Validation("stale_heartbeat", "Heartbeat SentAt is too old to establish an online lease.");
        if (request.Runtime.ChangedAt > request.SentAt + MaximumClockSkew ||
            request.Runtime.LastCommandCompletedAt > request.SentAt + MaximumClockSkew)
            throw DomainException.Validation("invalid_runtime_timestamp", "Runtime timestamps cannot be later than the heartbeat.");
    }

    /// <summary>把空白可选文本规范化为空值，避免持久化无意义空字符串。</summary>
    /// <param name="value">可选文本。</param>
    /// <returns>非空白原值或空值。</returns>
    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
