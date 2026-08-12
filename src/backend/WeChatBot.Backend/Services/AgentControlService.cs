using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;

namespace WeChatBot.Backend.Services;

public sealed class AgentControlService(
    AppDbContext db,
    TenantContext tenant,
    TimeProvider timeProvider,
    AuditService audit,
    IOptions<AuthOptions> authOptions)
{
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumHeartbeatAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RejectionAuditInterval = TimeSpan.FromMinutes(5);
    private const int HeartbeatUpdateAttempts = 8;

    public async Task<AgentHeartbeatResponse> RecordHeartbeatAsync(
        AgentHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        ValidateTimestamps(request, now);

        var agentId = request.AgentId.Trim();
        var normalizedAgentId = NormalizeAgentId(agentId);
        var weChatInstanceId = request.WeChatInstanceId.Trim();
        var registration = await EnsureRegistrationAsync(
            agentId,
            normalizedAgentId,
            weChatInstanceId,
            now,
            cancellationToken);

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

        var normalizedAgentId = NormalizeAgentId(agentId);
        var normalizedInstanceId = weChatInstanceId.Trim();
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
                x => x.NormalizedAgentId == normalizedAgentId,
                cancellationToken);
        if (registration is null ||
            !registration.IsEnabled ||
            !string.Equals(registration.WeChatInstanceId, normalizedInstanceId, StringComparison.Ordinal))
        {
            throw DomainException.Conflict(
                "agent_lease_unavailable",
                "The active Agent binding could not be verified.");
        }

        var state = await db.AgentHeartbeatStates.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.AgentRegistrationId == registration.Id,
                cancellationToken);
        if (state is null ||
            state.ReceivedAt < onlineAfter ||
            state.SentAt < onlineAfter ||
            state.ReceivedAt < controlState.UpdatedAt ||
            state.RuntimeState != AgentOperatingState.Healthy ||
            !state.DryRun)
        {
            throw DomainException.Conflict(
                "agent_lease_unavailable",
                "The active Agent binding could not be verified.");
        }
    }

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
                registration.IsEnabled && state?.ReceivedAt >= onlineAfter);
        }).ToList();
    }

    public async Task<AgentListItem> RegisterAsync(
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
            if (!string.Equals(existing.AgentId, agentId, StringComparison.Ordinal) ||
                !string.Equals(existing.WeChatInstanceId, weChatInstanceId, StringComparison.Ordinal) ||
                !string.Equals(existing.ConfigurationVersion, configurationVersion, StringComparison.Ordinal))
            {
                throw DomainException.Conflict(
                    "agent_registration_conflict",
                    "The AgentId is already registered with different immutable registration data.");
            }
            return ToListItem(existing, null, timeProvider.GetUtcNow() - OnlineWindow);
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
        var registration = new AgentRegistration
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            AgentId = agentId,
            NormalizedAgentId = normalizedAgentId,
            WeChatInstanceId = weChatInstanceId,
            IsEnabled = true,
            ConfigurationVersion = configurationVersion,
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
            if (existing is not null &&
                string.Equals(existing.AgentId, agentId, StringComparison.Ordinal) &&
                string.Equals(existing.WeChatInstanceId, weChatInstanceId, StringComparison.Ordinal) &&
                string.Equals(existing.ConfigurationVersion, configurationVersion, StringComparison.Ordinal))
            {
                return ToListItem(existing, null, now - OnlineWindow);
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
        return ToListItem(registration, null, now - OnlineWindow);
    }

    public static string NormalizeAgentId(string agentId) => agentId.Trim().ToUpperInvariant();

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
        registration.IsEnabled && state?.ReceivedAt >= onlineAfter);

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

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
