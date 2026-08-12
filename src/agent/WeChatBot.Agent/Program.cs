using System.Text.Json;
using WeChatBot.Agent.Automation;
using WeChatBot.Agent.Configuration;
using WeChatBot.Agent.Execution;
using WeChatBot.Agent.Heartbeat;
using WeChatBot.Agent.Runtime;

namespace WeChatBot.Agent;

public static class Program
{
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

        return await RunAgentAsync(options, uiProbe).ConfigureAwait(false);
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

    private static async Task<int> RunAgentAsync(AgentOptions options, IWeChatUiProbe uiProbe)
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
        HttpClient? httpClient = null;
        if (options.HeartbeatUri is not null)
        {
            var apiKey = options.ControlPlaneApiKey
                ?? throw new InvalidOperationException("Validated heartbeat credentials are unavailable.");
            httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            var pump = new AgentHeartbeatPump(
                new HttpAgentHeartbeatClient(httpClient, options.HeartbeatUri, apiKey),
                runtime,
                () => (executor.QueueDepth, executor.ActiveExecutions),
                options.AgentId,
                options.WeChatInstanceId,
                options.DryRun,
                options.HeartbeatInterval,
                missedHeartbeatLimit: 4);
            heartbeatTask = pump.RunAsync(shutdown.Token);
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
                var completed = await Task.WhenAny(shutdownWait, heartbeatTask).ConfigureAwait(false);
                if (completed == heartbeatTask && !shutdown.IsCancellationRequested)
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
            await executor.StopAsync().ConfigureAwait(false);
            try
            {
                if (heartbeatTask is not null)
                {
                    await heartbeatTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                // Normal heartbeat shutdown.
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Heartbeat pump failed ({exception.GetType().Name}).");
                exitCode = 4;
            }

            httpClient?.Dispose();
        }

        return exitCode;
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
              --control-plane-api-key=value        Required with heartbeat URI; prefer the environment variable.
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
