using System.Threading.Channels;
using WeChatBot.Agent.Contracts;
using WeChatBot.Agent.Runtime;

namespace WeChatBot.Agent.Execution;

public sealed record CommandPreflightResult(bool Allowed, string Code, string Summary)
{
    public static CommandPreflightResult Allow(string code = "PREFLIGHT_OK", string summary = "Preflight passed.") =>
        new(true, code, summary);

    public static CommandPreflightResult Reject(string code, string summary) => new(false, code, summary);
}

public sealed class CommandExecutionContext
{
    private readonly IAgentExecutionPermit _executionPermit;
    private readonly bool _externalActionsAllowed;
    private int _externalActionStarted;

    internal CommandExecutionContext(
        IAgentExecutionPermit executionPermit,
        bool externalActionsAllowed)
    {
        _executionPermit = executionPermit;
        _externalActionsAllowed = externalActionsAllowed;
    }

    public bool ExternalActionStarted => Volatile.Read(ref _externalActionStarted) != 0;

    public bool TryBeginExternalAction()
    {
        if (!_externalActionsAllowed)
        {
            return false;
        }

        if (!_executionPermit.TryBeginExternalAction())
        {
            return false;
        }

        Interlocked.Exchange(ref _externalActionStarted, 1);
        return true;
    }
}

public sealed record CommandHandlerResult(
    CommandResultStatus Status,
    string Code,
    string Summary,
    IReadOnlyDictionary<string, string>? Data = null);

public interface IAgentCommandHandler
{
    bool CanHandle(IAgentCommand command);

    ValueTask<CommandPreflightResult> PreflightAsync(
        IAgentCommand command,
        CancellationToken cancellationToken);

    ValueTask<CommandHandlerResult> ExecuteAsync(
        IAgentCommand command,
        CommandExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed class SerializedCommandExecutor : IAsyncDisposable
{
    private readonly Channel<WorkItem> _channel;
    private readonly IReadOnlyList<IAgentCommandHandler> _handlers;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly AgentRuntimeState _runtimeState;
    private readonly string _boundWeChatInstanceId;
    private readonly bool _dryRun;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;
    private int _started;
    private int _queueDepth;
    private int _activeExecutions;
    private int _disposed;

    public SerializedCommandExecutor(
        IEnumerable<IAgentCommandHandler> handlers,
        IIdempotencyStore idempotencyStore,
        AgentRuntimeState runtimeState,
        string boundWeChatInstanceId,
        bool dryRun = true,
        TimeProvider? timeProvider = null,
        int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundWeChatInstanceId);
        _handlers = handlers.ToArray();
        _idempotencyStore = idempotencyStore;
        _runtimeState = runtimeState;
        _boundWeChatInstanceId = boundWeChatInstanceId;
        _dryRun = dryRun;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public int QueueDepth => Math.Max(0, Volatile.Read(ref _queueDepth));

    public int ActiveExecutions => Volatile.Read(ref _activeExecutions);

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The command executor has already been started.");
        }

        _worker = Task.Run(ProcessQueueAsync);
    }

    public async ValueTask<CommandExecutionResult> EnqueueAsync(
        IAgentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Volatile.Read(ref _started) == 0)
        {
            throw new InvalidOperationException("Start the command executor before enqueueing commands.");
        }

        var completion = new TaskCompletionSource<CommandExecutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(command, cancellationToken, completion);
        Interlocked.Increment(ref _queueDepth);
        try
        {
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _queueDepth);
            throw;
        }

        return await completion.Task.ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        RequestStop();
        if (_worker is not null)
        {
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        RequestStop();
        if (_worker is not null)
        {
            await _worker.ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }

    private void RequestStop()
    {
        _runtimeState.MarkStopping(_timeProvider.GetUtcNow());
        _channel.Writer.TryComplete();
        if (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync())
            {
                Interlocked.Decrement(ref _queueDepth);
                Interlocked.Increment(ref _activeExecutions);
                try
                {
                    item.Completion.TrySetResult(await ExecuteOneAsync(item).ConfigureAwait(false));
                }
                catch (Exception exception)
                {
                    item.Completion.TrySetException(exception);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeExecutions);
                }
            }
        }
        finally
        {
            while (_channel.Reader.TryRead(out var pending))
            {
                Interlocked.Decrement(ref _queueDepth);
                var now = _timeProvider.GetUtcNow();
                pending.Completion.TrySetResult(CommandExecutionResult.Create(
                    pending.Command.Metadata.CommandId,
                    CommandResultStatus.Canceled,
                    "AGENT_STOPPED",
                    "The agent stopped before the command started.",
                    now,
                    now));
            }
        }
    }

    private async Task<CommandExecutionResult> ExecuteOneAsync(WorkItem item)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var command = item.Command;
        var validationErrors = AgentCommandValidator.Validate(command, startedAt);
        if (validationErrors.Count > 0)
        {
            return Finish(CommandResultStatus.Rejected, "COMMAND_INVALID", string.Join(" ", validationErrors));
        }

        if (!string.Equals(
            command.Metadata.WeChatInstanceId,
            _boundWeChatInstanceId,
            StringComparison.Ordinal))
        {
            return Finish(
                CommandResultStatus.Rejected,
                "WECHAT_INSTANCE_MISMATCH",
                "The command targets a different WeChat instance than this executor binding.");
        }

        if (command.Metadata.ExpiresAt is { } expiresAt && expiresAt <= startedAt)
        {
            return Finish(CommandResultStatus.Rejected, "COMMAND_EXPIRED", "The command expired before execution.");
        }

        var matchingHandlers = _handlers.Where(candidate => candidate.CanHandle(command)).Take(2).ToArray();
        if (matchingHandlers.Length == 0)
        {
            return Finish(CommandResultStatus.Rejected, "COMMAND_HANDLER_NOT_FOUND", "No handler accepts this command.");
        }

        if (matchingHandlers.Length > 1)
        {
            return Finish(
                CommandResultStatus.Rejected,
                "COMMAND_HANDLER_AMBIGUOUS",
                "More than one handler accepts this command; execution was refused.");
        }

        var handler = matchingHandlers[0];

        var runtime = _runtimeState.Snapshot();
        if (runtime.State is AgentOperatingState.PausedOperator
            or AgentOperatingState.PausedControlPlane
            or AgentOperatingState.PausedUnknownUi
            or AgentOperatingState.Maintenance
            or AgentOperatingState.Stopping)
        {
            return Finish(CommandResultStatus.Paused, runtime.ReasonCode, runtime.Reason);
        }

        using var timeout = new CancellationTokenSource(GetEffectiveTimeout(command.Metadata, startedAt));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            item.CancellationToken,
            _shutdown.Token);

        CommandPreflightResult preflight;
        try
        {
            preflight = await handler.PreflightAsync(command, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FinishCancellation(false);
        }
        catch (Exception exception)
        {
            return Finish(
                CommandResultStatus.Failed,
                "PREFLIGHT_FAILED",
                $"Command preflight failed ({exception.GetType().Name}); no external action was attempted.");
        }

        if (!preflight.Allowed)
        {
            return Finish(CommandResultStatus.Paused, preflight.Code, preflight.Summary);
        }

        if (!_runtimeState.TryAcquireExecutionPermit(out var executionPermit))
        {
            runtime = _runtimeState.Snapshot();
            return Finish(CommandResultStatus.Paused, runtime.ReasonCode, runtime.Reason);
        }

        using var executionScope = executionPermit!;

        IdempotencyBeginResult begin;
        try
        {
            begin = await _idempotencyStore.TryBeginAsync(
                command.Metadata.IdempotencyKey,
                command.Metadata.CommandId,
                CommandContentFingerprint.Compute(command),
                startedAt,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FinishCancellation(false);
        }
        catch (Exception exception)
        {
            return Finish(
                CommandResultStatus.Failed,
                "IDEMPOTENCY_CLAIM_FAILED",
                $"The durable idempotency claim failed ({exception.GetType().Name}); execution was refused.");
        }

        if (begin.Disposition == IdempotencyDisposition.Conflict)
        {
            return Finish(
                CommandResultStatus.Rejected,
                "IDEMPOTENCY_CONTENT_CONFLICT",
                "The idempotency key is already bound to different command content.");
        }

        if (begin.Disposition == IdempotencyDisposition.Completed)
        {
            return begin.CachedResult!;
        }

        if (begin.Disposition == IdempotencyDisposition.InProgress)
        {
            return Finish(
                CommandResultStatus.Indeterminate,
                "IDEMPOTENCY_IN_PROGRESS",
                "This idempotency key was already started and has no final checkpoint; manual review is required.");
        }

        var context = new CommandExecutionContext(executionScope, externalActionsAllowed: !_dryRun);
        CommandExecutionResult result;
        try
        {
            var handled = await handler.ExecuteAsync(command, context, linked.Token).ConfigureAwait(false);
            result = Finish(
                handled.Status,
                handled.Code,
                handled.Summary,
                context.ExternalActionStarted,
                handled.Data);
        }
        catch (OperationCanceledException)
        {
            result = FinishCancellation(context.ExternalActionStarted);
        }
        catch (Exception exception)
        {
            result = Finish(
                context.ExternalActionStarted ? CommandResultStatus.Indeterminate : CommandResultStatus.Failed,
                context.ExternalActionStarted ? "ACTION_RESULT_UNKNOWN" : "COMMAND_FAILED",
                context.ExternalActionStarted
                    ? "Execution failed after an external action began; automatic retry is forbidden."
                    : $"Command execution failed ({exception.GetType().Name}).",
                context.ExternalActionStarted);
        }

        try
        {
            await _idempotencyStore.CompleteAsync(
                command.Metadata.IdempotencyKey,
                result,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = Finish(
                context.ExternalActionStarted ? CommandResultStatus.Indeterminate : CommandResultStatus.Failed,
                "IDEMPOTENCY_FINALIZE_FAILED",
                context.ExternalActionStarted
                    ? "The durable result checkpoint failed after an action began; manual review is required."
                    : $"The durable result checkpoint failed ({exception.GetType().Name}); no external action was attempted.",
                context.ExternalActionStarted);
        }

        _runtimeState.RecordCommandCompletion(result.Code, result.CompletedAt);
        return result;

        CommandExecutionResult Finish(
            CommandResultStatus status,
            string code,
            string summary,
            bool externalActionAttempted = false,
            IReadOnlyDictionary<string, string>? data = null)
        {
            var dictionary = data is null
                ? null
                : new Dictionary<string, string>(data, StringComparer.Ordinal);
            return CommandExecutionResult.Create(
                command.Metadata.CommandId,
                status,
                code,
                summary,
                startedAt,
                _timeProvider.GetUtcNow(),
                externalActionAttempted,
                dictionary);
        }

        CommandExecutionResult FinishCancellation(bool externalActionAttempted)
        {
            if (externalActionAttempted)
            {
                return Finish(
                    CommandResultStatus.Indeterminate,
                    "ACTION_CANCELED_RESULT_UNKNOWN",
                    "Cancellation occurred after an external action began; manual review is required.",
                    true);
            }

            if (item.CancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested)
            {
                return Finish(
                    CommandResultStatus.Canceled,
                    "COMMAND_CANCELED",
                    "The command was canceled before an external action began.");
            }

            return Finish(
                CommandResultStatus.TimedOut,
                "COMMAND_TIMEOUT",
                "The command exceeded its timeout or expiry deadline.");
        }
    }

    private TimeSpan GetEffectiveTimeout(CommandMetadata metadata, DateTimeOffset startedAt)
    {
        if (metadata.ExpiresAt is not { } expiresAt)
        {
            return metadata.Timeout;
        }

        var untilExpiry = expiresAt - startedAt;
        return untilExpiry < metadata.Timeout ? untilExpiry : metadata.Timeout;
    }

    private sealed record WorkItem(
        IAgentCommand Command,
        CancellationToken CancellationToken,
        TaskCompletionSource<CommandExecutionResult> Completion);
}
