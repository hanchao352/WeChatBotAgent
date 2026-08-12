using System.Net;
using System.Net.Http.Json;
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
                $"--control-plane-api-key={secret}"
            ],
            static _ => null);
        var handler = new RecordingHttpHandler(secret, HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var client = new HttpAgentHeartbeatClient(
            httpClient,
            options.HeartbeatUri!,
            options.ControlPlaneApiKey!);

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
                $"--control-plane-api-key={secret}"
            ],
            static _ => null);
        using var httpClient = new HttpClient(
            new RecordingHttpHandler(secret, HttpStatusCode.Unauthorized));
        var client = new HttpAgentHeartbeatClient(
            httpClient,
            options.HeartbeatUri!,
            options.ControlPlaneApiKey!);

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
        int missedLimit) =>
        new(
            client,
            runtime,
            () => (0, 0),
            "agent-test",
            "wechat-test",
            dryRun: true,
            TimeSpan.FromMilliseconds(10),
            missedLimit);

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
