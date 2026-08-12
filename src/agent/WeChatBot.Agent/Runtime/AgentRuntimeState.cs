namespace WeChatBot.Agent.Runtime;

public enum AgentOperatingState
{
    Starting,
    Healthy,
    PausedUnknownUi,
    PausedControlPlane,
    PausedOperator,
    Maintenance,
    Stopping
}

public sealed record AgentRuntimeSnapshot(
    AgentOperatingState State,
    string ReasonCode,
    string Reason,
    DateTimeOffset ChangedAt,
    DateTimeOffset? LastCommandCompletedAt,
    string? LastCommandCode);

public interface IAgentExecutionPermit : IDisposable
{
    bool TryBeginExternalAction();
}

public sealed class AgentRuntimeState
{
    private readonly Lock _sync = new();
    private int _activeExecutionPermits;
    private AgentRuntimeSnapshot _snapshot = new(
        AgentOperatingState.Starting,
        "STARTING",
        "Agent startup is in progress.",
        DateTimeOffset.UtcNow,
        null,
        null);

    public AgentRuntimeSnapshot Snapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    public bool TryMarkHealthy(string reason, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_snapshot.State is not (AgentOperatingState.Starting or AgentOperatingState.Healthy))
            {
                return false;
            }

            _snapshot = _snapshot with
            {
                State = AgentOperatingState.Healthy,
                ReasonCode = "HEALTHY",
                Reason = reason,
                ChangedAt = now
            };
            return true;
        }
    }

    public bool ResumeAfterVerifiedSelfCheck(string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A resume reason is required.", nameof(reason));
        }

        lock (_sync)
        {
            if (_snapshot.State != AgentOperatingState.PausedUnknownUi)
            {
                return false;
            }

            _snapshot = _snapshot with
            {
                State = AgentOperatingState.Healthy,
                ReasonCode = "HEALTHY",
                Reason = reason,
                ChangedAt = now
            };
            return true;
        }
    }

    public bool ResumeAfterControlPlaneAccepted(string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A resume reason is required.", nameof(reason));
        }

        lock (_sync)
        {
            if (_snapshot.State != AgentOperatingState.PausedControlPlane)
            {
                return false;
            }

            _snapshot = _snapshot with
            {
                State = AgentOperatingState.Healthy,
                ReasonCode = "HEALTHY",
                Reason = reason,
                ChangedAt = now
            };
            return true;
        }
    }

    public void PauseForUnknownUi(string reasonCode, string reason, DateTimeOffset now) =>
        TransitionToPaused(AgentOperatingState.PausedUnknownUi, reasonCode, reason, now);

    public void PauseForControlPlane(string reason, DateTimeOffset now) =>
        TransitionToPaused(AgentOperatingState.PausedControlPlane, "CONTROL_PLANE_UNAVAILABLE", reason, now);

    public void PauseByOperator(string reason, DateTimeOffset now) =>
        TransitionToPaused(AgentOperatingState.PausedOperator, "OPERATOR_PAUSE", reason, now);

    public void EnterMaintenance(string reason, DateTimeOffset now) =>
        TransitionToPaused(AgentOperatingState.Maintenance, "MAINTENANCE", reason, now);

    public void MarkStopping(DateTimeOffset now)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                State = AgentOperatingState.Stopping,
                ReasonCode = "STOPPING",
                Reason = "Agent shutdown is in progress.",
                ChangedAt = now
            };
        }
    }

    public void RecordCommandCompletion(string code, DateTimeOffset now)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                LastCommandCode = code,
                LastCommandCompletedAt = now
            };
        }
    }

    public bool TryAcquireExecutionPermit(out IAgentExecutionPermit? permit)
    {
        lock (_sync)
        {
            if (_snapshot.State != AgentOperatingState.Healthy)
            {
                permit = null;
                return false;
            }

            _activeExecutionPermits++;
            permit = new ExecutionPermit(this);
            return true;
        }
    }

    private void TransitionToPaused(
        AgentOperatingState state,
        string reasonCode,
        string reason,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A pause reason is required.", nameof(reason));
        }

        lock (_sync)
        {
            if (_snapshot.State == AgentOperatingState.Stopping)
            {
                return;
            }

            if (PausePriority(_snapshot.State) > PausePriority(state))
            {
                return;
            }

            _snapshot = _snapshot with
            {
                State = state,
                ReasonCode = reasonCode,
                Reason = reason,
                ChangedAt = now
            };
        }
    }

    private static int PausePriority(AgentOperatingState state) => state switch
    {
        AgentOperatingState.Stopping => 100,
        AgentOperatingState.PausedOperator => 90,
        AgentOperatingState.Maintenance => 80,
        AgentOperatingState.PausedUnknownUi => 70,
        AgentOperatingState.PausedControlPlane => 60,
        _ => 0
    };

    private void ReleaseExecutionPermit()
    {
        lock (_sync)
        {
            if (_activeExecutionPermits <= 0)
            {
                throw new InvalidOperationException("Execution permit accounting is inconsistent.");
            }

            _activeExecutionPermits--;
        }
    }

    private bool TryBeginExternalAction(ExecutionPermit permit)
    {
        lock (_sync)
        {
            if (_snapshot.State != AgentOperatingState.Healthy
                || permit.IsDisposed
                || permit.ExternalActionStarted)
            {
                return false;
            }

            permit.ExternalActionStarted = true;
            return true;
        }
    }

    private sealed class ExecutionPermit(AgentRuntimeState owner) : IAgentExecutionPermit
    {
        private int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public bool ExternalActionStarted { get; set; }

        public bool TryBeginExternalAction() => owner.TryBeginExternalAction(this);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ReleaseExecutionPermit();
            }
        }
    }
}
