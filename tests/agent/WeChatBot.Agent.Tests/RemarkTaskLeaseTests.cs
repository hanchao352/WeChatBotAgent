using System.Net;
using System.Text;
using System.Text.Json;
using WeChatBot.Agent.Automation;
using WeChatBot.Agent.Configuration;
using WeChatBot.Agent.Contracts;
using WeChatBot.Agent.Execution;
using WeChatBot.Agent.Leases;
using WeChatBot.Agent.Runtime;

namespace WeChatBot.Agent.Tests;

/// <summary>
/// 验证 Agent 备注租约客户端的凭据保护和 dry-run 消费安全边界。
/// </summary>
public sealed class RemarkTaskLeaseTests
{
    /// <summary>
    /// 验证一个控制面后台泵率先失败时，宿主仍等待另一个泵完成关闭后才传播异常。
    /// </summary>
    [Fact]
    public async Task Control_plane_shutdown_waits_for_every_started_pump_after_one_fails()
    {
        // 使用异步延续避免测试线程内联完成，从而真实观察等待状态。
        var remainingPump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedPump = Task.FromException(new InvalidOperationException("simulated heartbeat failure"));

        var shutdown = Program.AwaitControlPlaneShutdownAsync(failedPump, remainingPump.Task);

        Assert.False(shutdown.IsCompleted);
        remainingPump.SetResult();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => shutdown);
        Assert.Equal("simulated heartbeat failure", exception.Message);
    }

    /// <summary>
    /// 验证租约客户端只在请求头发送 API Key，令牌只在正文发送且不会进入 URL。
    /// </summary>
    [Fact]
    public async Task Http_client_keeps_credentials_out_of_urls_and_claim_payloads()
    {
        const string apiKey = "remark-task-http-secret";
        const string leaseToken = "remark-task-lease-token-that-must-stay-private";
        var taskId = Guid.NewGuid();
        var handler = new RecordingHandler(apiKey, leaseToken, taskId);
        using var httpClient = new HttpClient(handler);
        var options = AgentOptions.Parse(
            [
                "--heartbeat-uri=https://control.example/api/agents/heartbeat",
                "--remark-task-lease-uri=https://control.example/api/agents",
                $"--agent-credential={apiKey}"
            ],
            static _ => null);
        var client = new HttpRemarkTaskLeaseClient(
            httpClient,
            options.RemarkTaskLeaseUri!,
            "agent-a",
            "wx-a",
            options.AgentCredential!);

        var claimed = await client.ClaimAsync(CancellationToken.None);
        _ = await client.ReleaseAsync(claimed!, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.True(request.ApiKeyMatched));
        Assert.DoesNotContain(apiKey, string.Join('|', handler.Requests.Select(request => request.Uri)), StringComparison.Ordinal);
        Assert.DoesNotContain(leaseToken, handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain(leaseToken, handler.Requests[1].Uri, StringComparison.Ordinal);
        Assert.Contains(leaseToken, handler.Requests[1].Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证 dry-run 轮询只预览命令并释放租约，绝不调用完成端点或声明真实成功。
    /// </summary>
    [Fact]
    public async Task Dry_run_pump_previews_and_releases_without_reporting_success()
    {
        var now = DateTimeOffset.UtcNow;
        var leasedTask = new LeasedRemarkTask(
            Guid.NewGuid(),
            LeasedRemarkTargetKind.Contact,
            Guid.NewGuid(),
            "wx-external-contact",
            "C-100-Lease contact",
            "Lease contact",
            null,
            "opaque-lease-token-for-test-only",
            now.AddMinutes(1),
            1,
            2);
        var leaseClient = new StubLeaseClient(leasedTask);
        var runtime = new AgentRuntimeState();
        Assert.True(runtime.TryMarkHealthy("lease test", now));
        await using var executor = new SerializedCommandExecutor(
            [new DryRunCommandHandler(new AllowingSafetyGate(), true)],
            new InMemoryIdempotencyStore(),
            runtime,
            "wx-test",
            dryRun: true);
        executor.Start();
        var pump = new RemarkTaskLeasePump(
            leaseClient,
            executor,
            runtime,
            "wx-test",
            TimeSpan.FromSeconds(5));

        var processed = await pump.PollOnceAsync(CancellationToken.None);

        Assert.True(processed);
        Assert.Equal(1, leaseClient.ClaimCalls);
        Assert.Equal(1, leaseClient.ReleaseCalls);
        Assert.Same(leasedTask, leaseClient.ReleasedTask);
    }

    [Fact]
    public async Task Dry_run_background_pump_does_not_consume_pending_tasks_across_multiple_intervals()
    {
        var now = DateTimeOffset.UtcNow;
        var leaseClient = new OldestPendingLeaseClient(
        [
            CreateLeasedTask(now, "wx-oldest-contact", attemptCount: 1),
            CreateLeasedTask(now, "wx-later-contact", attemptCount: 1)
        ]);
        var runtime = new AgentRuntimeState();
        Assert.True(runtime.TryMarkHealthy("lease test", now));
        await using var executor = new SerializedCommandExecutor(
            [new DryRunCommandHandler(new AllowingSafetyGate(), true)],
            new InMemoryIdempotencyStore(),
            runtime,
            "wx-test",
            dryRun: true);
        executor.Start();
        var pump = new RemarkTaskLeasePump(
            leaseClient,
            executor,
            runtime,
            "wx-test",
            TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));

        await pump.RunAsync(cancellation.Token);

        Assert.Equal(0, leaseClient.ClaimCalls);
        Assert.Equal(0, leaseClient.ReleaseCalls);
        Assert.Equal(2, leaseClient.PendingTaskCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_client_normalizes_claim_and_release_timeouts_as_http_failures(bool release)
    {
        using var httpClient = new HttpClient(new BlockingHandler())
        {
            Timeout = TimeSpan.FromMilliseconds(50)
        };
        var client = CreateHttpLeaseClient(httpClient);
        var task = CreateLeasedTask(DateTimeOffset.UtcNow, "wx-timeout-contact", attemptCount: 1);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            if (release)
            {
                _ = await client.ReleaseAsync(task, CancellationToken.None);
            }
            else
            {
                _ = await client.ClaimAsync(CancellationToken.None);
            }
        });

        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_client_preserves_host_cancellation_for_claim_and_release(bool release)
    {
        using var httpClient = new HttpClient(new BlockingHandler())
        {
            Timeout = TimeSpan.FromMinutes(1)
        };
        var client = CreateHttpLeaseClient(httpClient);
        var task = CreateLeasedTask(DateTimeOffset.UtcNow, "wx-cancelled-contact", attemptCount: 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (release)
            {
                _ = await client.ReleaseAsync(task, cancellation.Token);
            }
            else
            {
                _ = await client.ClaimAsync(cancellation.Token);
            }
        });
    }

    [Fact]
    public async Task Explicit_preview_does_not_claim_when_runtime_is_not_healthy()
    {
        var now = DateTimeOffset.UtcNow;
        var leaseClient = new StubLeaseClient(
            CreateLeasedTask(now, "wx-paused-contact", attemptCount: 1));
        var runtime = new AgentRuntimeState();
        await using var executor = new SerializedCommandExecutor(
            [new DryRunCommandHandler(new AllowingSafetyGate(), true)],
            new InMemoryIdempotencyStore(),
            runtime,
            "wx-test",
            dryRun: true);
        executor.Start();
        var pump = new RemarkTaskLeasePump(
            leaseClient,
            executor,
            runtime,
            "wx-test",
            TimeSpan.FromSeconds(5));

        var processed = await pump.PollOnceAsync(CancellationToken.None);

        Assert.False(processed);
        Assert.Equal(0, leaseClient.ClaimCalls);
        Assert.Equal(0, leaseClient.ReleaseCalls);
    }

    /// <summary>
    /// 验证租约轮询地址必须与心跳同时配置，避免无健康门禁地启动领取循环。
    /// </summary>
    [Fact]
    public void Remark_task_polling_requires_a_heartbeat_endpoint()
    {
        var exception = Assert.Throws<ArgumentException>(() => AgentOptions.Parse(
            [
                "--remark-task-lease-uri=https://control.example/api/agents",
                "--control-plane-api-key=test-secret"
            ],
            static _ => null));

        Assert.Contains("heartbeat URI", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRemarkTaskLeaseClient CreateHttpLeaseClient(HttpClient httpClient)
    {
        var options = AgentOptions.Parse(
        [
            "--heartbeat-uri=https://control.example/api/agents/heartbeat",
            "--remark-task-lease-uri=https://control.example/api/agents",
            "--agent-credential=remark-task-http-test-secret"
        ],
            static _ => null);
        return new HttpRemarkTaskLeaseClient(
            httpClient,
            options.RemarkTaskLeaseUri!,
            "agent-a",
            "wx-a",
            options.AgentCredential!);
    }

    private static LeasedRemarkTask CreateLeasedTask(
        DateTimeOffset now,
        string targetExternalId,
        int attemptCount) => new(
        Guid.NewGuid(),
        LeasedRemarkTargetKind.Contact,
        Guid.NewGuid(),
        targetExternalId,
        "C-100-Lease contact",
        "Lease contact",
        null,
        "opaque-lease-token-for-test-only",
        now.AddMinutes(1),
        attemptCount,
        2);

    /// <summary>始终允许预检的安全门，仅用于证明 dry-run 命令被串行执行器接收。</summary>
    private sealed class AllowingSafetyGate : IUiSafetyGate
    {
        /// <inheritdoc />
        public ValueTask<UiSafetyDecision> VerifyAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new UiSafetyDecision(
                true,
                "TEST_SAFE",
                "Test safety gate passed.",
                new UiProbeResult(
                    UiRecognitionStatus.CompatibleMainWindow,
                    "TEST_SAFE",
                    "Test safety gate passed.",
                    null,
                    null,
                    DateTimeOffset.UtcNow)));
    }

    /// <summary>记录领取和释放次数的内存租约客户端。</summary>
    private sealed class StubLeaseClient(LeasedRemarkTask task) : IRemarkTaskLeaseClient
    {
        /// <summary>获取领取调用次数。</summary>
        public int ClaimCalls { get; private set; }

        /// <summary>获取释放调用次数。</summary>
        public int ReleaseCalls { get; private set; }

        /// <summary>获取最后一次释放的任务。</summary>
        public LeasedRemarkTask? ReleasedTask { get; private set; }

        /// <inheritdoc />
        public ValueTask<LeasedRemarkTask?> ClaimAsync(CancellationToken cancellationToken)
        {
            ClaimCalls++;
            return ValueTask.FromResult<LeasedRemarkTask?>(task);
        }

        /// <inheritdoc />
        public ValueTask<RemarkTaskReleaseResult> ReleaseAsync(
            LeasedRemarkTask releasedTask,
            CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            ReleasedTask = releasedTask;
            return ValueTask.FromResult(new RemarkTaskReleaseResult(releasedTask.Version + 1));
        }
    }

    private sealed class OldestPendingLeaseClient(IReadOnlyList<LeasedRemarkTask> tasks)
        : IRemarkTaskLeaseClient
    {
        private LeasedRemarkTask? _activeTask;

        public int ClaimCalls { get; private set; }

        public int ReleaseCalls { get; private set; }

        public int PendingTaskCount => tasks.Count;

        public ValueTask<LeasedRemarkTask?> ClaimAsync(CancellationToken cancellationToken)
        {
            ClaimCalls++;
            _activeTask = tasks[0];
            return ValueTask.FromResult<LeasedRemarkTask?>(_activeTask);
        }

        public ValueTask<RemarkTaskReleaseResult> ReleaseAsync(
            LeasedRemarkTask releasedTask,
            CancellationToken cancellationToken)
        {
            Assert.Same(_activeTask, releasedTask);
            ReleaseCalls++;
            _activeTask = null;
            return ValueTask.FromResult(new RemarkTaskReleaseResult(releasedTask.Version + 1));
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking HTTP handler resumed without cancellation.");
        }
    }

    /// <summary>记录 HTTP 请求的 URI、正文和鉴权匹配结果。</summary>
    private sealed class RecordingHandler(
        string expectedApiKey,
        string leaseToken,
        Guid taskId) : HttpMessageHandler
    {
        /// <summary>获取按发送顺序记录的请求。</summary>
        public List<RecordedRequest> Requests { get; } = [];

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var apiKeyMatched = request.Headers.TryGetValues("X-Api-Key", out var values) &&
                                values.Single() == expectedApiKey;
            Requests.Add(new RecordedRequest(request.RequestUri!.AbsoluteUri, body, apiKeyMatched));

            if (request.RequestUri.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal))
            {
                var payload = JsonSerializer.Serialize(new
                {
                    taskId,
                    targetKind = "contact",
                    targetId = Guid.NewGuid(),
                    targetExternalId = "wx-external-contact",
                    generatedRemark = "expected remark",
                    expectedTargetDisplayName = "expected contact",
                    originalWeChatRemark = (string?)null,
                    leaseToken,
                    leaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
                    attemptCount = 1,
                    version = 2
                });
                return JsonResponse(HttpStatusCode.OK, payload);
            }

            return JsonResponse(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new { taskId, status = "pending", version = 3 }));
        }

        /// <summary>创建 JSON HTTP 响应。</summary>
        private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>表示一次已脱敏测试记录，不保存真实生产凭据。</summary>
    private sealed record RecordedRequest(string Uri, string Body, bool ApiKeyMatched);
}
