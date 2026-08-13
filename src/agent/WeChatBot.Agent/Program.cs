using System.Text.Json;
using WeChatBot.Agent.Automation;
using WeChatBot.Agent.Configuration;
using WeChatBot.Agent.Execution;
using WeChatBot.Agent.Heartbeat;
using WeChatBot.Agent.Leases;
using WeChatBot.Agent.Runtime;

namespace WeChatBot.Agent;

/// <summary>提供 Agent 命令行入口、启动自检和控制面后台任务生命周期管理。</summary>
public static class Program
{
    /// <summary>解析配置并运行诊断、自检或正式 Agent 主循环。</summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>适合进程管理器判断启动、配置或运行故障的退出码。</returns>
    public static async Task<int> Main(string[] args)
    {
        AgentOptions options;
        try
        {
            options = AgentOptions.Parse(args);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or UriFormatException)
        {
            Console.Error.WriteLine($"Configuration error: {exception.Message}");
            return 64;
        }

        if (options.Mode == AgentRunMode.Help)
        {
            PrintHelp();
            return 0;
        }

        var processDetector = new WeChatProcessDetector();
        var profile = UiCompatibilityProfile.StrictDefault(
            options.SupportedVersionPrefixes,
            options.RequiredAutomationIdFingerprints);
        var uiProbe = new WeChatUiProbe(processDetector, profile);

        if (options.Mode == AgentRunMode.Diagnose)
        {
            return RunDiagnostics(processDetector, options.UiProbeTimeout);
        }

        var selfCheck = new EnvironmentSelfCheck(new WindowsDesktopSessionProbe(), uiProbe, options.DryRun);
        var report = selfCheck.Run(options.UiProbeTimeout);
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        if (options.Mode == AgentRunMode.SelfCheck || !report.Ready)
        {
            return report.Ready ? 0 : 2;
        }

        return await RunAgentAsync(options, uiProbe, selfCheck).ConfigureAwait(false);
    }

    private static int RunDiagnostics(IWeChatProcessDetector processDetector, TimeSpan timeout)
    {
        var processes = processDetector.DetectMainProcesses();
        if (processes.Count != 1)
        {
            Console.Error.WriteLine(processes.Count == 0
                ? "No WeChat main process was found."
                : "More than one WeChat main process was found; refusing an ambiguous diagnostic capture.");
            return 2;
        }

        try
        {
            var tree = new ControlTreeDiagnostics().Capture(processes[0], timeout);
            Console.WriteLine(ControlTreeDiagnostics.ToJson(tree));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Diagnostic capture failed ({exception.GetType().Name}).");
            return 2;
        }
    }

    private static async Task<int> RunAgentAsync(
        AgentOptions options,
        IWeChatUiProbe uiProbe,
        IAgentRecoverySelfCheck recoverySelfCheck)
    {
        if (!WeChatInstanceLease.TryAcquire(options.WeChatInstanceId, out var acquiredLease))
        {
            Console.Error.WriteLine("Another agent process already owns this WeChat instance binding.");
            return 3;
        }

        using var instanceLease = acquiredLease;
        var runtime = new AgentRuntimeState();
        if (options.HeartbeatUri is null)
        {
            runtime.TryMarkHealthy("Startup self-check passed in standalone mode.", DateTimeOffset.UtcNow);
        }
        else
        {
            runtime.PauseForControlPlane(
                "Waiting for the control plane to accept the initial heartbeat lease.",
                DateTimeOffset.UtcNow);
        }
        var gate = new FlaUiSafetyGate(uiProbe, runtime, TimeProvider.System, options.UiProbeTimeout);
        var instanceStorageKey = WeChatInstanceIdentity.ToStorageKey(options.WeChatInstanceId);
        var journalPath = Path.Combine(
            options.StateDirectory,
            "instances",
            instanceStorageKey,
            "idempotency.db");
        var store = new SqliteIdempotencyStore(journalPath);
        await using var executor = new SerializedCommandExecutor(
            [new DryRunCommandHandler(gate, options.DryRun)],
            store,
            runtime,
            options.WeChatInstanceId,
            options.DryRun);
        executor.Start();

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        Task? heartbeatTask = null;
        Task? remarkTaskLeaseTask = null;
        HttpClient? httpClient = null;
        if (options.HeartbeatUri is not null)
        {
            var agentCredential = options.AgentCredential
                ?? throw new InvalidOperationException("Validated heartbeat credentials are unavailable.");
            httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            var pump = new AgentHeartbeatPump(
                new HttpAgentHeartbeatClient(httpClient, options.HeartbeatUri, agentCredential),
                runtime,
                () => (executor.QueueDepth, executor.ActiveExecutions),
                options.AgentId,
                options.WeChatInstanceId,
                options.DryRun,
                options.HeartbeatInterval,
                missedHeartbeatLimit: 4,
                recoverySelfCheck: recoverySelfCheck,
                recoverySelfCheckTimeout: options.UiProbeTimeout);
            heartbeatTask = pump.RunAsync(shutdown.Token);

            if (options.RemarkTaskLeaseUri is not null)
            {
                var leasePump = new RemarkTaskLeasePump(
                    new HttpRemarkTaskLeaseClient(
                        httpClient,
                        options.RemarkTaskLeaseUri,
                        options.AgentId,
                        options.WeChatInstanceId,
                        agentCredential),
                    executor,
                    runtime,
                    options.WeChatInstanceId,
                    TimeSpan.FromSeconds(5));
                remarkTaskLeaseTask = leasePump.RunAsync(shutdown.Token);
            }
        }

        var exitCode = 0;
        try
        {
            var shutdownWait = Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
            if (heartbeatTask is null)
            {
                await shutdownWait.ConfigureAwait(false);
            }
            else
            {
                var supervisedTasks = remarkTaskLeaseTask is null
                    ? new[] { shutdownWait, heartbeatTask }
                    : new[] { shutdownWait, heartbeatTask, remarkTaskLeaseTask };
                var completed = await Task.WhenAny(supervisedTasks).ConfigureAwait(false);
                if (completed != shutdownWait && !shutdown.IsCancellationRequested)
                {
                    runtime.PauseForControlPlane(
                        "The heartbeat pump stopped unexpectedly.",
                        DateTimeOffset.UtcNow);
                    Console.Error.WriteLine("Heartbeat supervision stopped the agent host.");
                    exitCode = 4;
                    shutdown.Cancel();
                }
                else
                {
                    await shutdownWait.ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Normal Ctrl+C shutdown.
        }
        finally
        {
            // 先通知所有后台泵停止，避免租约泵在执行器关闭后继续领取或入队。
            shutdown.Cancel();
            try
            {
                await AwaitControlPlaneShutdownAsync(heartbeatTask, remarkTaskLeaseTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                // Normal heartbeat shutdown.
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Control-plane supervision failed ({exception.GetType().Name}).");
                exitCode = 4;
            }

            await executor.StopAsync().ConfigureAwait(false);
            httpClient?.Dispose();
        }

        return exitCode;
    }

    /// <summary>
    /// 等待所有已启动的控制面后台泵结束，确保任一泵失败时也不会跳过其余泵的关闭等待。
    /// </summary>
    /// <param name="heartbeatTask">心跳泵任务；未配置控制面时为空。</param>
    /// <param name="remarkTaskLeaseTask">备注任务租约泵任务；未配置租约轮询时为空。</param>
    internal static Task AwaitControlPlaneShutdownAsync(
        Task? heartbeatTask,
        Task? remarkTaskLeaseTask)
    {
        // Task.WhenAll 会在返回前观察并等待所有任务，即使其中一个任务已率先失败。
        var controlPlaneTasks = new[] { heartbeatTask, remarkTaskLeaseTask }
            .OfType<Task>();
        return Task.WhenAll(controlPlaneTasks);
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            WeChatBot.Agent (.NET 10, Windows, FlaUI UIA3)

              --self-check                         Run environment and UI compatibility checks (default).
              --diagnose                           Print a redacted UI Automation control tree.
              --run                                Run the heartbeat and serialized command host.
              --dry-run[=true|false]                Defaults to true. Live mutation is rejected in this build.
              --supported-version-prefixes=4.0.5   Comma-separated tested WeChat versions.
              --required-automation-id-fingerprints=f1,f2
                                                   Required hashes from an approved redacted UI signature.
              --heartbeat-uri=https://host/path    Optional control-plane heartbeat endpoint.
              --remark-task-lease-uri=https://host/api/agents
                                                   Optional dry-run remark-task lease base endpoint.
              --agent-credential=value             Per-Agent credential; prefer WECHATBOT_AGENT_CREDENTIAL.
              --control-plane-api-key=value        Deprecated alias for credential migration only.
              --agent-id=value                     Stable agent identity.
              --instance-id=value                  Bound WeChat instance identity.
              --state-directory=path               Durable idempotency journal directory.
              --help                               Show this help.
            """);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
