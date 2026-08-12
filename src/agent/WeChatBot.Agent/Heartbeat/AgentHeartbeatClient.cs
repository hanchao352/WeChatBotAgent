using System.Net.Http.Json;
using System.Text.Json;
using WeChatBot.Agent.Automation;
using WeChatBot.Agent.Configuration;
using WeChatBot.Agent.Runtime;

namespace WeChatBot.Agent.Heartbeat;

public sealed record AgentHeartbeat(
    string AgentId,
    string WeChatInstanceId,
    DateTimeOffset SentAt,
    AgentRuntimeSnapshot Runtime,
    int QueueDepth,
    int ActiveExecutions,
    bool DryRun,
    string AgentVersion);

public sealed record AgentHeartbeatResponse(
    bool Accepted,
    bool EmergencyStop,
    string? ConfigurationVersion);

public interface IAgentHeartbeatClient
{
    ValueTask<AgentHeartbeatResponse> SendAsync(
        AgentHeartbeat heartbeat,
        CancellationToken cancellationToken);
}

public sealed class HttpAgentHeartbeatClient : IAgentHeartbeatClient
{
    public const string ApiKeyHeaderName = "X-Api-Key";

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly SecretValue _apiKey;

    public HttpAgentHeartbeatClient(HttpClient httpClient, Uri endpoint, SecretValue apiKey)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(apiKey);

        _httpClient = httpClient;
        _endpoint = endpoint;
        _apiKey = apiKey;
    }

    public async ValueTask<AgentHeartbeatResponse> SendAsync(
        AgentHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(heartbeat)
        };
        request.Headers.Add(ApiKeyHeaderName, _apiKey.Reveal());

        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentHeartbeatResponse>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Heartbeat response body was empty.");
    }
}

public sealed class AgentHeartbeatPump(
    IAgentHeartbeatClient client,
    AgentRuntimeState runtimeState,
    Func<(int QueueDepth, int ActiveExecutions)> executorMetrics,
    string agentId,
    string weChatInstanceId,
    bool dryRun,
    TimeSpan interval,
    int missedHeartbeatLimit,
    TimeProvider? timeProvider = null,
    IAgentRecoverySelfCheck? recoverySelfCheck = null,
    TimeSpan? recoverySelfCheckTimeout = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _recoverySelfCheckTimeout = recoverySelfCheckTimeout ?? TimeSpan.FromSeconds(5);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(missedHeartbeatLimit, 1);
        var consecutiveFailures = 0;
        using var timer = new PeriodicTimer(interval, _timeProvider);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var metrics = executorMetrics();
                var heartbeat = new AgentHeartbeat(
                    agentId,
                    weChatInstanceId,
                    _timeProvider.GetUtcNow(),
                    runtimeState.Snapshot(),
                    metrics.QueueDepth,
                    metrics.ActiveExecutions,
                    dryRun,
                    typeof(AgentHeartbeatPump).Assembly.GetName().Version?.ToString() ?? "unknown");
                var response = await client.SendAsync(heartbeat, cancellationToken).ConfigureAwait(false);
                consecutiveFailures = 0;

                if (response.EmergencyStop)
                {
                    runtimeState.PauseByOperator(
                        "The control plane requested an emergency stop.",
                        _timeProvider.GetUtcNow());
                }
                else if (!response.Accepted)
                {
                    runtimeState.PauseForControlPlane(
                        "The control plane rejected the agent heartbeat.",
                        _timeProvider.GetUtcNow());
                }
                else
                {
                    runtimeState.ResumeAfterControlPlaneDecision(
                        response.Accepted,
                        response.EmergencyStop,
                        "The control plane accepted the current agent lease.",
                        _timeProvider.GetUtcNow());

                    if (runtimeState.Snapshot().State == AgentOperatingState.PausedUnknownUi)
                    {
                        RunControlledRecoverySelfCheck(cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                    or TaskCanceledException
                    or InvalidDataException
                    or JsonException)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= missedHeartbeatLimit)
                {
                    runtimeState.PauseForControlPlane(
                        $"Heartbeat failed {consecutiveFailures} consecutive times.",
                        _timeProvider.GetUtcNow());
                }
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void RunControlledRecoverySelfCheck(CancellationToken cancellationToken)
    {
        if (recoverySelfCheck is null)
        {
            return;
        }

        try
        {
            var report = recoverySelfCheck.Run(_recoverySelfCheckTimeout, cancellationToken);
            if (report.Ready)
            {
                runtimeState.ResumeAfterVerifiedSelfCheck(
                    "The controlled environment and UI self-check passed.",
                    _timeProvider.GetUtcNow());
                return;
            }

            var failure = report.Findings.FirstOrDefault(finding =>
                finding.Severity == SelfCheckSeverity.Critical && !finding.Passed);
            runtimeState.PauseForUnknownUi(
                failure?.Code ?? report.UiProbe.Code,
                failure?.Summary ?? report.UiProbe.Summary,
                _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            runtimeState.PauseForUnknownUi(
                "RECOVERY_SELF_CHECK_FAILED",
                $"The controlled recovery self-check failed ({exception.GetType().Name}).",
                _timeProvider.GetUtcNow());
        }
    }
}
