using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatBot.Agent.Configuration;
using WeChatBot.Agent.Contracts;
using WeChatBot.Agent.Execution;
using WeChatBot.Agent.Heartbeat;
using WeChatBot.Agent.Runtime;

namespace WeChatBot.Agent.Leases;

/// <summary>表示后端备注任务目标类型。</summary>
public enum LeasedRemarkTargetKind
{
    /// <summary>联系人目标。</summary>
    Contact,

    /// <summary>群目标。</summary>
    Group
}

/// <summary>
/// 表示后端授予 Agent 的备注任务租约快照。
/// </summary>
public sealed record LeasedRemarkTask(
    Guid TaskId,
    LeasedRemarkTargetKind TargetKind,
    Guid TargetId,
    string TargetExternalId,
    string GeneratedRemark,
    string ExpectedTargetDisplayName,
    string? OriginalWeChatRemark,
    string LeaseToken,
    DateTimeOffset LeaseExpiresAt,
    int AttemptCount,
    long Version);

/// <summary>表示释放备注任务租约所需的并发版本。</summary>
public sealed record RemarkTaskReleaseResult(long Version);

/// <summary>
/// 抽象备注任务租约控制面，使轮询流程可以在不依赖 HTTP 的测试中验证安全行为。
/// </summary>
public interface IRemarkTaskLeaseClient
{
    /// <summary>领取下一项可用任务，队列为空时返回空值。</summary>
    ValueTask<LeasedRemarkTask?> ClaimAsync(CancellationToken cancellationToken);

    /// <summary>释放当前仍有效的租约。</summary>
    ValueTask<RemarkTaskReleaseResult> ReleaseAsync(
        LeasedRemarkTask task,
        CancellationToken cancellationToken);
}

/// <summary>
/// 使用 Agent 控制面凭据调用后端备注任务租约端点。
/// </summary>
public sealed class HttpRemarkTaskLeaseClient : IRemarkTaskLeaseClient
{
    /// <summary>负责发送不跟随重定向请求的共享 HTTP 客户端。</summary>
    private readonly HttpClient _httpClient;

    /// <summary>形如 `/api/agents` 的租约路由根地址。</summary>
    private readonly Uri _endpointRoot;

    /// <summary>路由中的 Agent 标识。</summary>
    private readonly string _agentId;

    /// <summary>请求正文中的微信实例绑定。</summary>
    private readonly string _weChatInstanceId;

    /// <summary>只在请求头中发送的控制面凭据。</summary>
    private readonly SecretValue _apiKey;

    /// <summary>按后端约定将枚举序列化为 camelCase 字符串。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// 创建 HTTP 租约客户端，并验证所有身份和地址参数。
    /// </summary>
    public HttpRemarkTaskLeaseClient(
        HttpClient httpClient,
        Uri endpointRoot,
        string agentId,
        string weChatInstanceId,
        SecretValue apiKey)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpointRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(weChatInstanceId);
        ArgumentNullException.ThrowIfNull(apiKey);
        if (!endpointRoot.IsAbsoluteUri)
        {
            throw new ArgumentException("Remark-task lease endpoint must be absolute.", nameof(endpointRoot));
        }

        _httpClient = httpClient;
        _endpointRoot = endpointRoot;
        _agentId = agentId;
        _weChatInstanceId = weChatInstanceId;
        _apiKey = apiKey;
    }

    /// <inheritdoc />
    public async ValueTask<LeasedRemarkTask?> ClaimAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"{Uri.EscapeDataString(_agentId)}/remark-tasks/claim",
            new RemarkTaskClaimBody(_weChatInstanceId));
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LeasedRemarkTask>(JsonOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException("Remark-task claim response body was empty.");
    }

    /// <inheritdoc />
    public async ValueTask<RemarkTaskReleaseResult> ReleaseAsync(
        LeasedRemarkTask task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        using var request = CreateRequest(
            HttpMethod.Post,
            $"{Uri.EscapeDataString(_agentId)}/remark-tasks/{task.TaskId:D}/release",
            new RemarkTaskLeaseBody(_weChatInstanceId, task.LeaseToken, task.Version));
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RemarkTaskReleaseResult>(JsonOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException("Remark-task release response body was empty.");
    }

    /// <summary>
    /// 创建不会把 API Key 或租约令牌写入 URL 的 JSON 请求。
    /// </summary>
    private HttpRequestMessage CreateRequest<TBody>(HttpMethod method, string relativePath, TBody body)
    {
        var normalizedRoot = _endpointRoot.AbsoluteUri.TrimEnd('/') + "/";
        var request = new HttpRequestMessage(method, new Uri(new Uri(normalizedRoot), relativePath))
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        request.Headers.Add(HttpAgentHeartbeatClient.ApiKeyHeaderName, _apiKey.Reveal());
        return request;
    }

    /// <summary>
    /// 发送租约请求，并将未由宿主令牌触发的 HTTP 超时规范化为可恢复的传输错误。
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The remark-task lease request timed out.", exception);
        }
    }

    /// <summary>表示领取端点的最小请求正文。</summary>
    private sealed record RemarkTaskClaimBody(string WeChatInstanceId);

    /// <summary>表示释放端点的租约持有证明。</summary>
    private sealed record RemarkTaskLeaseBody(
        string WeChatInstanceId,
        string LeaseToken,
        long ExpectedVersion);
}

/// <summary>
/// 为备注任务提供显式 dry-run 诊断入口；后台运行模式不会领取生产队列任务。
/// </summary>
public sealed class RemarkTaskLeasePump(
    IRemarkTaskLeaseClient client,
    SerializedCommandExecutor executor,
    AgentRuntimeState runtime,
    string weChatInstanceId,
    TimeSpan interval,
    TimeProvider? timeProvider = null)
{
    /// <summary>用于计算命令期限和轮询时间的时钟。</summary>
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// 显式执行单次诊断预览；常驻后台运行不会调用该入口。
    /// </summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>领取并释放过任务时为 <see langword="true"/>，队列为空时为 <see langword="false"/>。</returns>
    public async Task<bool> PollOnceAsync(CancellationToken cancellationToken)
    {
        if (runtime.Snapshot().State != AgentOperatingState.Healthy) return false;

        var task = await client.ClaimAsync(cancellationToken).ConfigureAwait(false);
        if (task is null) return false;

        try
        {
            var now = _timeProvider.GetUtcNow();
            var command = new UpdateRemarkCommand(
                new CommandMetadata(
                    $"remark-task-{task.TaskId:N}-attempt-{task.AttemptCount}",
                    $"remark-task:{task.TaskId:N}:attempt:{task.AttemptCount}",
                    weChatInstanceId,
                    now,
                    task.LeaseExpiresAt,
                    TimeSpan.FromSeconds(10),
                    $"lease-{task.TaskId:N}"),
                task.TargetKind == LeasedRemarkTargetKind.Contact
                    ? RemarkTargetKind.Contact
                    : RemarkTargetKind.Group,
                task.TargetExternalId,
                task.ExpectedTargetDisplayName,
                task.OriginalWeChatRemark,
                task.GeneratedRemark);
            var result = await executor.EnqueueAsync(command, cancellationToken).ConfigureAwait(false);

            // 当前构建只能返回 DryRun/拒绝等非成功结果；无论结果如何都释放租约供后续真实执行器处理。
            if (result.Status == CommandResultStatus.Succeeded)
            {
                throw new InvalidOperationException(
                    "The dry-run remark-task pump received an impossible successful mutation result.");
            }
            return true;
        }
        finally
        {
            // 只要调用未被关闭取消，就尽力释放；令牌从不进入异常、日志或命令数据。
            if (!cancellationToken.IsCancellationRequested)
            {
                _ = await client.ReleaseAsync(task, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 在强制 dry-run 构建中保持后台组件存活，但不领取或释放生产任务。
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (interval < TimeSpan.FromMilliseconds(100) || interval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                "Remark-task polling interval must be between 100 milliseconds and one minute.");
        }

        using var timer = new PeriodicTimer(interval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // 强制 dry-run 构建只维持受监督的后台生命周期，不接触生产任务队列。
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 宿主关闭时正常结束；当前构建不会在后台接触生产任务队列。
        }
    }

}
