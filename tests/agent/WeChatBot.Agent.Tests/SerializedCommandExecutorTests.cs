using WeChatBot.Agent.Contracts;
using WeChatBot.Agent.Execution;
using WeChatBot.Agent.Runtime;

namespace WeChatBot.Agent.Tests;

public sealed class SerializedCommandExecutorTests
{
    [Fact]
    public async Task ExecutesOnlyOneHandlerAtATime()
    {
        var runtime = HealthyRuntime();
        var handler = new TrackingHandler(TimeSpan.FromMilliseconds(40));
        await using var executor = new SerializedCommandExecutor(
            [handler],
            new InMemoryIdempotencyStore(),
            runtime,
            "wechat-test",
            capacity: 16);
        executor.Start();

        var tasks = Enumerable.Range(0, 8)
            .Select(index => executor.EnqueueAsync(CreateCommand(index)).AsTask())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.Equal(CommandResultStatus.DryRun, result.Status));
        Assert.Equal(1, handler.MaximumConcurrency);
        Assert.Equal(8, handler.ExecutionCount);
    }

    [Fact]
    public async Task DuplicateIdempotencyKeyReturnsCachedResultWithoutExecutingAgain()
    {
        var handler = new TrackingHandler(TimeSpan.Zero);
        await using var executor = new SerializedCommandExecutor(
            [handler],
            new InMemoryIdempotencyStore(),
            HealthyRuntime(),
            "wechat-test");
        executor.Start();
        var first = CreateCommand(1, "same-key");
        var duplicate = CreateCommand(2, "same-key");

        var firstResult = await executor.EnqueueAsync(first);
        var duplicateResult = await executor.EnqueueAsync(duplicate);

        Assert.Equal(first.Metadata.CommandId, firstResult.CommandId);
        Assert.Equal(firstResult, duplicateResult);
        Assert.Equal(1, handler.ExecutionCount);
    }

    [Fact]
    public async Task SameIdempotencyKeyWithDifferentContentIsRejected()
    {
        var handler = new TrackingHandler(TimeSpan.Zero);
        await using var executor = new SerializedCommandExecutor(
            [handler],
            new InMemoryIdempotencyStore(),
            HealthyRuntime(),
            "wechat-test",
            dryRun: false);
        executor.Start();
        var first = CreateCommand(1, "same-key");
        var conflict = CreateCommand(2, "same-key") with { GroupStableId = "different-group" };

        await executor.EnqueueAsync(first);
        var conflictResult = await executor.EnqueueAsync(conflict);

        Assert.Equal(CommandResultStatus.Rejected, conflictResult.Status);
        Assert.Equal("IDEMPOTENCY_CONTENT_CONFLICT", conflictResult.Code);
        Assert.Equal(1, handler.ExecutionCount);
    }

    [Fact]
    public async Task TimesOutAndDoesNotAttemptExternalAction()
    {
        var handler = new TrackingHandler(TimeSpan.FromSeconds(2));
        await using var executor = new SerializedCommandExecutor(
            [handler],
            new InMemoryIdempotencyStore(),
            HealthyRuntime(),
            "wechat-test");
        executor.Start();
        var command = CreateCommand(1) with
        {
            Metadata = CreateCommand(1).Metadata with { Timeout = TimeSpan.FromMilliseconds(100) }
        };

        var result = await executor.EnqueueAsync(command);

        Assert.Equal(CommandResultStatus.TimedOut, result.Status);
        Assert.Equal("COMMAND_TIMEOUT", result.Code);
        Assert.False(result.ExternalActionAttempted);
    }

    [Fact]
    public async Task CancellationAfterActionStartsIsIndeterminate()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new TrackingHandler(
            TimeSpan.FromSeconds(2),
            onStarted: context =>
            {
                Assert.True(context.TryBeginExternalAction());
                cancellation.Cancel();
            });
        await using var executor = new SerializedCommandExecutor(
            [handler],
            new InMemoryIdempotencyStore(),
            HealthyRuntime(),
            "wechat-test",
            dryRun: false);
        executor.Start();

        var result = await executor.EnqueueAsync(CreateCommand(1), cancellation.Token);

        Assert.Equal(CommandResultStatus.Indeterminate, result.Status);
        Assert.Equal("ACTION_CANCELED_RESULT_UNKNOWN", result.Code);
        Assert.True(result.ExternalActionAttempted);
    }

    [Fact]
    public async Task PausedRuntimeRejectsBeforeIdempotencyClaim()
    {
        var runtime = HealthyRuntime();
        runtime.PauseByOperator("maintenance approval", DateTimeOffset.UtcNow);
        var store = new InMemoryIdempotencyStore();
        var handler = new TrackingHandler(TimeSpan.Zero);
        await using var executor = new SerializedCommandExecutor([handler], store, runtime, "wechat-test");
        executor.Start();
        var command = CreateCommand(1);

        var paused = await executor.EnqueueAsync(command);
        runtime.TryMarkHealthy("ignored because operator pause is sticky", DateTimeOffset.UtcNow);
        var pausedAgain = await executor.EnqueueAsync(command);

        Assert.Equal(CommandResultStatus.Paused, paused.Status);
        Assert.Equal(CommandResultStatus.Paused, pausedAgain.Status);
        Assert.Equal(0, handler.ExecutionCount);
    }

    [Fact]
    public async Task CommandForAnotherWeChatInstanceIsRejected()
    {
        var handler = new TrackingHandler(TimeSpan.Zero);
        await using var executor = new SerializedCommandExecutor(
            [handler],
            new InMemoryIdempotencyStore(),
            HealthyRuntime(),
            "wechat-bound");
        executor.Start();

        var result = await executor.EnqueueAsync(CreateCommand(1));

        Assert.Equal(CommandResultStatus.Rejected, result.Status);
        Assert.Equal("WECHAT_INSTANCE_MISMATCH", result.Code);
        Assert.Equal(0, handler.ExecutionCount);
    }

    [Fact]
    public async Task PauseRaisedAtEndOfPreflightPreventsExecution()
    {
        var runtime = HealthyRuntime();
        var handler = new TrackingHandler(
            TimeSpan.Zero,
            onPreflight: () => runtime.PauseByOperator("emergency", DateTimeOffset.UtcNow));
        await using var executor = new SerializedCommandExecutor(
            [handler],
            new InMemoryIdempotencyStore(),
            runtime,
            "wechat-test");
        executor.Start();

        var result = await executor.EnqueueAsync(CreateCommand(1));

        Assert.Equal(CommandResultStatus.Paused, result.Status);
        Assert.Equal("OPERATOR_PAUSE", result.Code);
        Assert.Equal(0, handler.ExecutionCount);
    }

    [Fact]
    public async Task DisposeWaitsForWorkerAfterCanceledStopWait()
    {
        var handler = new BlockingHandler();
        var executor = new SerializedCommandExecutor(
            [handler],
            new InMemoryIdempotencyStore(),
            HealthyRuntime(),
            "wechat-test");
        executor.Start();
        var commandTask = executor.EnqueueAsync(CreateCommand(1)).AsTask();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var stopWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.StopAsync(stopWait.Token));
        var disposeTask = executor.DisposeAsync().AsTask();
        Assert.False(disposeTask.IsCompleted);

        handler.Release.TrySetResult();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));
        var result = await commandTask;
        Assert.Equal(CommandResultStatus.DryRun, result.Status);
    }

    [Fact]
    public async Task EnqueueAfterStopIsRejectedWithoutChangingQueueDepth()
    {
        await using var executor = new SerializedCommandExecutor(
            [new TrackingHandler(TimeSpan.Zero)],
            new InMemoryIdempotencyStore(),
            HealthyRuntime(),
            "wechat-test");
        executor.Start();
        await executor.StopAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.EnqueueAsync(CreateCommand(1)).AsTask());

        Assert.Contains("no longer accepts", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, executor.QueueDepth);
    }

    [Fact]
    public async Task StopCompletesQueuedCommandsWithoutExecutingThem()
    {
        var handler = new BlockingHandler();
        var executor = new SerializedCommandExecutor(
            [handler],
            new InMemoryIdempotencyStore(),
            HealthyRuntime(),
            "wechat-test",
            capacity: 2);
        executor.Start();
        var active = executor.EnqueueAsync(CreateCommand(1)).AsTask();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = executor.EnqueueAsync(CreateCommand(2)).AsTask();
        var stop = executor.StopAsync();

        Assert.False(stop.IsCompleted);
        Assert.False(queued.IsCompleted);

        handler.Release.TrySetResult();
        Assert.Equal(CommandResultStatus.DryRun, (await active).Status);
        var queuedResult = await queued.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CommandResultStatus.Paused, queuedResult.Status);
        Assert.Equal("STOPPING", queuedResult.Code);
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, executor.QueueDepth);
        await executor.DisposeAsync();
    }

    [Fact]
    public async Task StoppedExecutorCannotBeStarted()
    {
        await using var executor = new SerializedCommandExecutor(
            [new TrackingHandler(TimeSpan.Zero)],
            new InMemoryIdempotencyStore(),
            HealthyRuntime(),
            "wechat-test");
        await executor.StopAsync();

        var exception = Assert.Throws<InvalidOperationException>(executor.Start);

        Assert.Contains("cannot be restarted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmergencyPauseAfterPermitPreventsExternalActionBoundary()
    {
        var runtime = HealthyRuntime();
        var handler = new ExternalActionHandler();
        var store = new PauseAfterClaimStore(runtime);
        await using var executor = new SerializedCommandExecutor(
            [handler],
            store,
            runtime,
            "wechat-test",
            dryRun: false);
        executor.Start();

        var result = await executor.EnqueueAsync(CreateCommand(1));

        Assert.Equal(CommandResultStatus.Paused, result.Status);
        Assert.Equal(0, handler.ExternalActionCount);
        Assert.False(result.ExternalActionAttempted);
    }

    [Fact]
    public async Task DryRunExecutorNeverOpensExternalActionBoundary()
    {
        var handler = new ExternalActionHandler();
        await using var executor = new SerializedCommandExecutor(
            [handler],
            new InMemoryIdempotencyStore(),
            HealthyRuntime(),
            "wechat-test",
            dryRun: true);
        executor.Start();

        var result = await executor.EnqueueAsync(CreateCommand(1));

        Assert.Equal(CommandResultStatus.Paused, result.Status);
        Assert.Equal(0, handler.ExternalActionCount);
        Assert.False(result.ExternalActionAttempted);
    }

    private static AgentRuntimeState HealthyRuntime()
    {
        var runtime = new AgentRuntimeState();
        Assert.True(runtime.TryMarkHealthy("test", DateTimeOffset.UtcNow));
        return runtime;
    }

    private static ObserveMentionsCommand CreateCommand(int index, string? idempotencyKey = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ObserveMentionsCommand(
            new CommandMetadata(
                $"command-{index}",
                idempotencyKey ?? $"key-{index}",
                "wechat-test",
                now,
                now.AddMinutes(1),
                TimeSpan.FromSeconds(5),
                $"trace-{index}"),
            "group-stable-id",
            "group display",
            "bot display",
            null);
    }

    private sealed class TrackingHandler(
        TimeSpan delay,
        Action<CommandExecutionContext>? onStarted = null,
        Action? onPreflight = null) : IAgentCommandHandler
    {
        private int _active;
        private int _maximumConcurrency;
        private int _executionCount;

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public bool CanHandle(IAgentCommand command) => true;

        public ValueTask<CommandPreflightResult> PreflightAsync(
            IAgentCommand command,
            CancellationToken cancellationToken)
        {
            onPreflight?.Invoke();
            return ValueTask.FromResult(CommandPreflightResult.Allow());
        }

        public async ValueTask<CommandHandlerResult> ExecuteAsync(
            IAgentCommand command,
            CommandExecutionContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executionCount);
            var active = Interlocked.Increment(ref _active);
            SetMaximum(active);
            onStarted?.Invoke(context);
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                return new CommandHandlerResult(CommandResultStatus.DryRun, "TEST", "test");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void SetMaximum(int value)
        {
            var current = Volatile.Read(ref _maximumConcurrency);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref _maximumConcurrency, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed class BlockingHandler : IAgentCommandHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanHandle(IAgentCommand command) => true;

        public ValueTask<CommandPreflightResult> PreflightAsync(
            IAgentCommand command,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CommandPreflightResult.Allow());

        public async ValueTask<CommandHandlerResult> ExecuteAsync(
            IAgentCommand command,
            CommandExecutionContext context,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task;
            return new CommandHandlerResult(CommandResultStatus.DryRun, "TEST", "test");
        }
    }

    private sealed class ExternalActionHandler : IAgentCommandHandler
    {
        private int _externalActionCount;

        public int ExternalActionCount => Volatile.Read(ref _externalActionCount);

        public bool CanHandle(IAgentCommand command) => true;

        public ValueTask<CommandPreflightResult> PreflightAsync(
            IAgentCommand command,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CommandPreflightResult.Allow());

        public ValueTask<CommandHandlerResult> ExecuteAsync(
            IAgentCommand command,
            CommandExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!context.TryBeginExternalAction())
            {
                return ValueTask.FromResult(new CommandHandlerResult(
                    CommandResultStatus.Paused,
                    "EMERGENCY_STOP",
                    "External action boundary was closed."));
            }

            Interlocked.Increment(ref _externalActionCount);
            return ValueTask.FromResult(new CommandHandlerResult(
                CommandResultStatus.Succeeded,
                "ACTION_DONE",
                "action done"));
        }
    }

    private sealed class PauseAfterClaimStore(AgentRuntimeState runtime) : IIdempotencyStore
    {
        private readonly InMemoryIdempotencyStore _inner = new();

        public async ValueTask<IdempotencyBeginResult> TryBeginAsync(
            string idempotencyKey,
            string commandId,
            string commandFingerprint,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var result = await _inner.TryBeginAsync(
                idempotencyKey,
                commandId,
                commandFingerprint,
                now,
                cancellationToken);
            runtime.PauseByOperator("emergency", DateTimeOffset.UtcNow);
            return result;
        }

        public ValueTask CompleteAsync(
            string idempotencyKey,
            CommandExecutionResult result,
            CancellationToken cancellationToken) =>
            _inner.CompleteAsync(idempotencyKey, result, cancellationToken);
    }
}
