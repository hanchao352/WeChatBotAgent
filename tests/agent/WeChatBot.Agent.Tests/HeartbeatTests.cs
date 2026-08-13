using System.Net;
using System.Net.Http.Json;
using WeChatBot.Agent.Automation;
using WeChatBot.Agent.Heartbeat;
using WeChatBot.Agent.Runtime;
using System.Text.Json;
using WeChatBot.Agent.Configuration;

namespace WeChatBot.Agent.Tests;

public sealed class HeartbeatTests
{
    [Fact]
    public async Task HttpClientSendsApiKeyHeaderAndKeepsSecretOutOfPayload()
    {
        const string secret = "http-client-test-secret";
        var options = AgentOptions.Parse(
            [
                "--heartbeat-uri=https://control.example/api/agents/heartbeat",
                $"--agent-credential={secret}"
            ],
            static _ => null);
        var handler = new RecordingHttpHandler(secret, HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var client = new HttpAgentHeartbeatClient(
            httpClient,
            options.HeartbeatUri!,
            options.AgentCredential!);

        var response = await client.SendAsync(CreateHeartbeat(), CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.True(handler.ApiKeyMatched);
        Assert.True(handler.ApiKeyAbsentFromPayload);
        Assert.False(httpClient.DefaultRequestHeaders.Contains(HttpAgentHeartbeatClient.ApiKeyHeaderName));
    }

    [Fact]
    public async Task HttpFailureDoesNotExposeApiKeyInException()
    {
        const string secret = "http-error-test-secret";
        var options = AgentOptions.Parse(
            [
                "--heartbeat-uri=https://control.example/api/agents/heartbeat",
                $"--agent-credential={secret}"
            ],
            static _ => null);
        using var httpClient = new HttpClient(
            new RecordingHttpHandler(secret, HttpStatusCode.Unauthorized));
        var client = new HttpAgentHeartbeatClient(
            httpClient,
            options.HeartbeatUri!,
            options.AgentCredential!);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendAsync(CreateHeartbeat(), CancellationToken.None).AsTask());

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptedInitialHeartbeatOpensControlPlaneGate()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new AgentRuntimeState();
        runtime.PauseForControlPlane("awaiting initial lease", DateTimeOffset.UtcNow);
        var client = new StubHeartbeatClient((_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult(new AgentHeartbeatResponse(true, false, "config-1"));
        });
        var pump = CreatePump(client, runtime, missedLimit: 3);

        await pump.RunAsync(cancellation.Token);

        Assert.Equal(AgentOperatingState.Healthy, runtime.Snapshot().State);
    }

    [Fact]
    public async Task RejectedHeartbeatPausesControlPlane()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = HealthyRuntime();
        var client = new StubHeartbeatClient((_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult(new AgentHeartbeatResponse(false, false, null));
        });
        var pump = CreatePump(client, runtime, missedLimit: 3);

        await pump.RunAsync(cancellation.Token);

        Assert.Equal(AgentOperatingState.PausedControlPlane, runtime.Snapshot().State);
        Assert.Equal("CONTROL_PLANE_UNAVAILABLE", runtime.Snapshot().ReasonCode);
    }

    [Fact]
    public async Task EmergencyStopWinsEvenWhenHeartbeatIsRejected()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = HealthyRuntime();
        var client = new StubHeartbeatClient((_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult(new AgentHeartbeatResponse(false, true, null));
        });
        var pump = CreatePump(client, runtime, missedLimit: 3);

        await pump.RunAsync(cancellation.Token);

        Assert.Equal(AgentOperatingState.PausedOperator, runtime.Snapshot().State);
        Assert.Equal("OPERATOR_PAUSE", runtime.Snapshot().ReasonCode);
    }

    [Fact]
    public async Task ClearedEmergencyStopResumesOnlyAfterAcceptedHeartbeat()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = HealthyRuntime();
        var responses = new Queue<AgentHeartbeatResponse>(
        [
            new(true, true, "config-1"),
            new(false, false, "config-1"),
            new(true, false, "config-1")
        ]);
        var observedStates = new List<AgentOperatingState>();
        var client = new StubHeartbeatClient((_, _) =>
        {
            observedStates.Add(runtime.Snapshot().State);
            var response = responses.Dequeue();
            if (responses.Count == 0)
            {
                cancellation.Cancel();
            }
            return ValueTask.FromResult(response);
        });
        var pump = CreatePump(client, runtime, missedLimit: 3);

        await pump.RunAsync(cancellation.Token);

        Assert.Equal(
            [AgentOperatingState.Healthy, AgentOperatingState.PausedOperator, AgentOperatingState.PausedOperator],
            observedStates);
        Assert.Equal(AgentOperatingState.Healthy, runtime.Snapshot().State);
    }

    [Fact]
    public async Task UnknownUiPauseResumesOnlyAfterControlledSelfCheckPasses()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = HealthyRuntime();
        runtime.PauseForUnknownUi("UNKNOWN", "unknown surface", DateTimeOffset.UtcNow);
        var recovery = new StubRecoverySelfCheck(
        [
            CreateSelfCheckReport(ready: false),
            CreateSelfCheckReport(ready: true)
        ],
        cancellation);
        var client = new StubHeartbeatClient((_, _) =>
            ValueTask.FromResult(new AgentHeartbeatResponse(true, false, "config-1")));
        var pump = CreatePump(client, runtime, missedLimit: 3, recovery);

        await pump.RunAsync(cancellation.Token);

        Assert.Equal(2, recovery.Calls);
        Assert.Equal(AgentOperatingState.Healthy, runtime.Snapshot().State);
    }

    [Fact]
    public async Task UnknownUiPauseStaysClosedWhenNoRecoverySelfCheckIsConfigured()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = HealthyRuntime();
        runtime.PauseForUnknownUi("UNKNOWN", "unknown surface", DateTimeOffset.UtcNow);
        var client = new StubHeartbeatClient((_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult(new AgentHeartbeatResponse(true, false, "config-1"));
        });
        var pump = CreatePump(client, runtime, missedLimit: 3);

        await pump.RunAsync(cancellation.Token);

        Assert.Equal(AgentOperatingState.PausedUnknownUi, runtime.Snapshot().State);
    }

    [Fact]
    public async Task RepeatedHeartbeatFailuresPauseControlPlane()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = HealthyRuntime();
        var calls = 0;
        var client = new StubHeartbeatClient((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                cancellation.Cancel();
            }

            throw new HttpRequestException("simulated");
        });
        var pump = CreatePump(client, runtime, missedLimit: 2);

        await pump.RunAsync(cancellation.Token);

        Assert.Equal(2, calls);
        Assert.Equal(AgentOperatingState.PausedControlPlane, runtime.Snapshot().State);
    }

    [Fact]
    public async Task MalformedHeartbeatResponseCountsAsFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = HealthyRuntime();
        var client = new StubHeartbeatClient((_, _) =>
        {
            cancellation.Cancel();
            throw new JsonException("simulated malformed response");
        });
        var pump = CreatePump(client, runtime, missedLimit: 1);

        await pump.RunAsync(cancellation.Token);

        Assert.Equal(AgentOperatingState.PausedControlPlane, runtime.Snapshot().State);
    }

    private static AgentHeartbeatPump CreatePump(
        IAgentHeartbeatClient client,
        AgentRuntimeState runtime,
        int missedLimit,
        IAgentRecoverySelfCheck? recoverySelfCheck = null) =>
        new(
            client,
            runtime,
            () => (0, 0),
            "agent-test",
            "wechat-test",
            dryRun: true,
            TimeSpan.FromMilliseconds(10),
            missedLimit,
            recoverySelfCheck: recoverySelfCheck,
            recoverySelfCheckTimeout: TimeSpan.FromMilliseconds(50));

    private static EnvironmentSelfCheckReport CreateSelfCheckReport(bool ready)
    {
        var now = DateTimeOffset.UtcNow;
        var probe = new UiProbeResult(
            ready ? UiRecognitionStatus.CompatibleMainWindow : UiRecognitionStatus.UnknownSurface,
            ready ? "WECHAT_UI_COMPATIBLE" : "WECHAT_SURFACE_UNKNOWN",
            ready ? "compatible" : "unknown",
            null,
            null,
            now);
        return new EnvironmentSelfCheckReport(
            now,
            ready,
            [new SelfCheckFinding(probe.Code, SelfCheckSeverity.Critical, ready, probe.Summary)],
            probe);
    }

    private static AgentHeartbeat CreateHeartbeat() =>
        new(
            "agent-test",
            "wechat-test",
            DateTimeOffset.UtcNow,
            HealthyRuntime().Snapshot(),
            QueueDepth: 0,
            ActiveExecutions: 0,
            DryRun: true,
            AgentVersion: "test");

    private static AgentRuntimeState HealthyRuntime()
    {
        var runtime = new AgentRuntimeState();
        Assert.True(runtime.TryMarkHealthy("test", DateTimeOffset.UtcNow));
        return runtime;
    }

    private sealed class StubHeartbeatClient(
        Func<AgentHeartbeat, CancellationToken, ValueTask<AgentHeartbeatResponse>> send)
        : IAgentHeartbeatClient
    {
        public ValueTask<AgentHeartbeatResponse> SendAsync(
            AgentHeartbeat heartbeat,
            CancellationToken cancellationToken) => send(heartbeat, cancellationToken);
    }

    private sealed class StubRecoverySelfCheck(
        Queue<EnvironmentSelfCheckReport> reports,
        CancellationTokenSource cancellation) : IAgentRecoverySelfCheck
    {
        public StubRecoverySelfCheck(
            IEnumerable<EnvironmentSelfCheckReport> reports,
            CancellationTokenSource cancellation)
            : this(new Queue<EnvironmentSelfCheckReport>(reports), cancellation)
        {
        }

        public int Calls { get; private set; }

        public EnvironmentSelfCheckReport Run(
            TimeSpan uiTimeout,
            CancellationToken cancellationToken)
        {
            Calls++;
            var report = reports.Dequeue();
            if (reports.Count == 0)
            {
                cancellation.Cancel();
            }
            return report;
        }
    }

    private sealed class RecordingHttpHandler(
        string expectedApiKey,
        HttpStatusCode statusCode) : HttpMessageHandler
    {
        public bool ApiKeyMatched { get; private set; }

        public bool ApiKeyAbsentFromPayload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ApiKeyMatched = request.Headers.TryGetValues(
                    HttpAgentHeartbeatClient.ApiKeyHeaderName,
                    out var values)
                && values is not null
                && values.Count() == 1
                && string.Equals(values.Single(), expectedApiKey, StringComparison.Ordinal);
            var payload = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            ApiKeyAbsentFromPayload = !payload.Contains(expectedApiKey, StringComparison.Ordinal);

            return new HttpResponseMessage(statusCode)
            {
                Content = JsonContent.Create(new AgentHeartbeatResponse(true, false, "config-test"))
            };
        }
    }
}
