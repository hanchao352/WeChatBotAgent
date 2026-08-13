using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Services;

namespace WeChatBot.Backend.Tests;

/// <summary>
/// 覆盖备注任务租约协议的原子竞争、持有者校验、过期回收、完整载荷幂等完成、管理员接管和安全门禁。
/// </summary>
public sealed class RemarkTaskLeaseIntegrationTests
{
    /// <summary>按生产 JSON 约定序列化枚举，确保测试直接验证 HTTP 合同。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// 验证并发领取同一任务时只有一个 Agent 获得租约，另一个调用方看到空队列。
    /// </summary>
    [Fact]
    public async Task Concurrent_claims_grant_exactly_one_lease()
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        var task = await CreateReadyTaskAsync(admin);
        var first = await RegisterHealthyAgentAsync(agent, "claim-a");
        var second = await RegisterHealthyAgentAsync(agent, "claim-b");

        var responses = await Task.WhenAll(
            ClaimResponseAsync(agent, first),
            ClaimResponseAsync(agent, second));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.NoContent));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.RemarkTasks.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(item => item.Id == task.Id);
        Assert.Equal(1, stored.AttemptCount);
        Assert.NotNull(stored.LeaseTokenHash);
        Assert.DoesNotContain(
            stored.LeaseTokenHash!,
            await responses.Single(response => response.StatusCode == HttpStatusCode.OK)
                .Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 验证 SQLite 写锁持续到所有有限重试结束时，领取接口返回稳定的 409 领域错误而不是泄漏驱动异常。
    /// </summary>
    [Fact]
    public async Task Claim_returns_conflict_when_sqlite_write_lock_persists()
    {
        using var factory = new TestApplicationFactory(
            new Dictionary<string, string?>(),
            databaseDefaultTimeoutSeconds: 1);
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        _ = await CreateReadyTaskAsync(admin);
        var owner = await RegisterHealthyAgentAsync(agent, "claim-busy");

        var connectionString = $"Data Source={factory.DatabasePath};Default Timeout=1;Pooling=False";
        await using var blockerConnection = new SqliteConnection(connectionString);
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = blockerConnection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: true);
        await using (var lockCommand = blockerConnection.CreateCommand())
        {
            lockCommand.Transaction = blockerTransaction;
            lockCommand.CommandText = "UPDATE RemarkTasks SET Version = Version WHERE 0 = 1;";
            await lockCommand.ExecuteNonQueryAsync();
        }

        using var response = await ClaimResponseAsync(agent, owner);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("remark_task_claim_busy", body, StringComparison.Ordinal);

        await blockerTransaction.RollbackAsync();
    }

    /// <summary>
    /// 验证领取之外的租约写操作遇到持续 SQLite 写锁时返回稳定 409，而不是泄漏为 HTTP 500。
    /// </summary>
    /// <param name="operation">要验证的租约操作。</param>
    [Theory]
    [InlineData("renew")]
    [InlineData("release")]
    [InlineData("complete")]
    public async Task Lease_mutations_return_conflict_when_sqlite_write_lock_persists(string operation)
    {
        using var factory = new TestApplicationFactory(
            new Dictionary<string, string?>(),
            databaseDefaultTimeoutSeconds: 1);
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        var task = await CreateReadyTaskAsync(admin);
        var owner = await RegisterHealthyAgentAsync(agent, $"{operation}-busy");
        var claim = await ClaimAsync(agent, owner);

        await using var blockerConnection = await OpenWriteLockConnectionAsync(factory);
        await using var blockerTransaction = blockerConnection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: true);
        await AcquireRemarkTaskWriteLockAsync(blockerConnection, blockerTransaction);

        using var response = operation == "complete"
            ? await agent.PostAsJsonAsync(
                LeaseRoute(owner, task.Id, operation),
                new RemarkTaskLeaseCompleteRequest(
                    owner.InstanceId,
                    claim.LeaseToken,
                    claim.Version,
                    $"busy-{Guid.NewGuid():N}",
                    true,
                    task.GeneratedRemark,
                    null),
                JsonOptions)
            : await agent.PostAsJsonAsync(
                LeaseRoute(owner, task.Id, operation),
                new RemarkTaskLeaseRequest(owner.InstanceId, claim.LeaseToken, claim.Version),
                JsonOptions);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("database_write_busy", body, StringComparison.Ordinal);
        await blockerTransaction.RollbackAsync();
    }

    /// <summary>
    /// 验证领取响应返回任务创建时固化的外部 ID 和显示名称，而非后续变更后的目标值。
    /// </summary>
    [Fact]
    public async Task Claim_returns_persisted_target_identity_snapshots()
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        var task = await CreateReadyTaskAsync(admin);
        string originalExternalId;
        string originalDisplayName;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var storedTask = await db.RemarkTasks.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(item => item.Id == task.Id);
            originalExternalId = storedTask.TargetExternalId;
            originalDisplayName = storedTask.ExpectedTargetDisplayName;
            var contact = await db.Contacts.IgnoreQueryFilters().SingleAsync(item => item.Id == task.TargetId);
            contact.ExternalId = $"changed-{Guid.NewGuid():N}";
            contact.DisplayName = "Changed after task creation";
            contact.Version++;
            await db.SaveChangesAsync();
        }
        var owner = await RegisterHealthyAgentAsync(agent, "identity-snapshot");

        var claim = await ClaimAsync(agent, owner);

        Assert.Equal(originalExternalId, claim.TargetExternalId);
        Assert.Equal(originalDisplayName, claim.ExpectedTargetDisplayName);
    }

    /// <summary>
    /// 验证续租、非持有者拒绝、主动释放和再次认领构成完整状态转换。
    /// </summary>
    [Fact]
    public async Task Renew_release_and_reclaim_require_the_current_owner()
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        var task = await CreateReadyTaskAsync(admin);
        var owner = await RegisterHealthyAgentAsync(agent, "owner");
        var other = await RegisterHealthyAgentAsync(agent, "other");
        var claim = await ClaimAsync(agent, owner);

        using var stolenRenew = await agent.PostAsJsonAsync(
            LeaseRoute(other, task.Id, "renew"),
            new RemarkTaskLeaseRequest(other.InstanceId, claim.LeaseToken, claim.Version),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, stolenRenew.StatusCode);
        Assert.Contains("remark_task_lease_not_owned", await stolenRenew.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var renewedResponse = await agent.PostAsJsonAsync(
            LeaseRoute(owner, task.Id, "renew"),
            new RemarkTaskLeaseRequest(owner.InstanceId, claim.LeaseToken, claim.Version),
            JsonOptions);
        var renewedBody = await renewedResponse.Content.ReadAsStringAsync();
        Assert.True(renewedResponse.IsSuccessStatusCode, renewedBody);
        var renewed = JsonSerializer.Deserialize<RemarkTaskLeaseResponse>(renewedBody, JsonOptions)!;
        Assert.True(renewed.LeaseExpiresAt >= claim.LeaseExpiresAt);
        Assert.Equal(claim.Version + 1, renewed.Version);

        using var releasedResponse = await agent.PostAsJsonAsync(
            LeaseRoute(owner, task.Id, "release"),
            new RemarkTaskLeaseRequest(owner.InstanceId, claim.LeaseToken, renewed.Version),
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, releasedResponse.StatusCode);
        var reclaimed = await ClaimAsync(agent, other);
        Assert.Equal(task.Id, reclaimed.TaskId);
        Assert.Equal(2, reclaimed.AttemptCount);
        Assert.NotEqual(claim.LeaseToken, reclaimed.LeaseToken);
    }

    /// <summary>
    /// 验证过期租约不能续租或完成，任务可由其他健康 Agent 重新领取。
    /// </summary>
    [Fact]
    public async Task Expired_lease_is_rejected_and_can_be_reclaimed()
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        var task = await CreateReadyTaskAsync(admin);
        var first = await RegisterHealthyAgentAsync(agent, "expired-first");
        var second = await RegisterHealthyAgentAsync(agent, "expired-second");
        var claim = await ClaimAsync(agent, first);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.RemarkTasks.IgnoreQueryFilters()
                .Where(item => item.Id == task.Id)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.LeaseExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        using var expiredRenew = await agent.PostAsJsonAsync(
            LeaseRoute(first, task.Id, "renew"),
            new RemarkTaskLeaseRequest(first.InstanceId, claim.LeaseToken, claim.Version),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, expiredRenew.StatusCode);
        Assert.Contains("remark_task_lease_expired", await expiredRenew.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var reclaimed = await ClaimAsync(agent, second);
        Assert.Equal(task.Id, reclaimed.TaskId);
        Assert.Equal(2, reclaimed.AttemptCount);

        using var expiredComplete = await agent.PostAsJsonAsync(
            LeaseRoute(first, task.Id, "complete"),
            new RemarkTaskLeaseCompleteRequest(
                first.InstanceId,
                claim.LeaseToken,
                claim.Version,
                $"expired-{Guid.NewGuid():N}",
                true,
                task.GeneratedRemark,
                null),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, expiredComplete.StatusCode);
    }

    /// <summary>
    /// 验证成功完成同步更新目标备注、清除租约，且相同结果重试返回稳定响应。
    /// </summary>
    [Fact]
    public async Task Successful_completion_is_atomic_and_idempotent()
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        var task = await CreateReadyTaskAsync(admin);
        var owner = await RegisterHealthyAgentAsync(agent, "complete-success");
        var claim = await ClaimAsync(agent, owner);
        var resultId = $"result-{Guid.NewGuid():N}";
        var completion = new RemarkTaskLeaseCompleteRequest(
            owner.InstanceId,
            claim.LeaseToken,
            claim.Version,
            resultId,
            true,
            task.GeneratedRemark,
            null);

        using var first = await agent.PostAsJsonAsync(
            LeaseRoute(owner, task.Id, "complete"),
            completion,
            JsonOptions);
        using var replay = await agent.PostAsJsonAsync(
            LeaseRoute(owner, task.Id, "complete"),
            completion,
            JsonOptions);
        var firstBody = await first.Content.ReadAsStringAsync();
        var replayBody = await replay.Content.ReadAsStringAsync();
        Assert.True(first.IsSuccessStatusCode, firstBody);
        Assert.True(replay.IsSuccessStatusCode, replayBody);
        Assert.False(JsonSerializer.Deserialize<RemarkTaskLeaseCompletionResponse>(firstBody, JsonOptions)!.Replayed);
        Assert.True(JsonSerializer.Deserialize<RemarkTaskLeaseCompletionResponse>(replayBody, JsonOptions)!.Replayed);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedTask = await db.RemarkTasks.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(item => item.Id == task.Id);
        var contact = await db.Contacts.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(item => item.Id == task.TargetId);
        Assert.Equal(RemarkTaskStatus.Completed, storedTask.Status);
        Assert.Equal(resultId, storedTask.CompletionResultId);
        Assert.Null(storedTask.LeaseTokenHash);
        Assert.Equal(task.GeneratedRemark, contact.SystemRemark);
        Assert.Equal(task.GeneratedRemark, contact.CurrentWeChatRemark);
    }

    /// <summary>
    /// 验证结果标识和可选字段先规范化再参与重放比较，且成功标志、实际备注或失败原因任一变化都会产生冲突。
    /// </summary>
    [Fact]
    public async Task Completion_replay_requires_the_same_normalized_full_payload()
    {
        // 每个用例使用独立数据库，避免其他任务或结果标识干扰重放判断。
        using var factory = new TestApplicationFactory();
        // 管理员客户端负责创建具备自动备注权益的待处理任务。
        using var admin = factory.CreateAuthenticatedClient();
        // Agent 客户端负责建立健康绑定并调用租约完成协议。
        using var agent = factory.CreateAgentClient();
        // 待处理任务提供首次提交必须精确匹配的生成备注。
        var task = await CreateReadyTaskAsync(admin);
        // 健康 Agent 是首次提交和后续重放共同使用的调用方身份。
        var owner = await RegisterHealthyAgentAsync(agent, "strict-replay");
        // 租约提供完成请求所需的令牌和期望版本。
        var claim = await ClaimAsync(agent, owner);
        // 规范结果标识用于确认首尾空白不会成为不同的幂等身份。
        var resultId = $"strict-{Guid.NewGuid():N}";
        // 首次请求故意携带结果标识首尾空白和空白失败原因，二者应分别规范化为裁剪值和空值。
        var initialRequest = new RemarkTaskLeaseCompleteRequest(
            owner.InstanceId,
            claim.LeaseToken,
            claim.Version,
            $"  {resultId}  ",
            true,
            task.GeneratedRemark,
            "   ");

        // 首次提交必须写入唯一终态，而不是被误判为重放。
        using var initialResponse = await agent.PostAsJsonAsync(
            LeaseRoute(owner, task.Id, "complete"),
            initialRequest,
            JsonOptions);
        // 规范重放使用裁剪后的结果标识和空失败原因，应与首次规范载荷完全一致。
        using var normalizedReplayResponse = await agent.PostAsJsonAsync(
            LeaseRoute(owner, task.Id, "complete"),
            initialRequest with { ResultId = resultId, FailureReason = null },
            JsonOptions);
        // 修改成功结果的 AppliedRemark，用于验证成功载荷也会参与幂等比较。
        using var changedRemarkResponse = await agent.PostAsJsonAsync(
            LeaseRoute(owner, task.Id, "complete"),
            initialRequest with { ResultId = resultId, AppliedRemark = $"{task.GeneratedRemark}-changed", FailureReason = null },
            JsonOptions);
        // 在成功结果中加入非空 FailureReason，用于验证成功载荷仍要求该字段的规范值为空。
        using var changedFailureResponse = await agent.PostAsJsonAsync(
            LeaseRoute(owner, task.Id, "complete"),
            initialRequest with { ResultId = resultId, FailureReason = "unexpected failure" },
            JsonOptions);
        // 将同一结果改报为失败，用于验证 Succeeded 也是结果身份的一部分。
        using var changedStatusResponse = await agent.PostAsJsonAsync(
            LeaseRoute(owner, task.Id, "complete"),
            initialRequest with { ResultId = resultId, Succeeded = false, AppliedRemark = null, FailureReason = "failed" },
            JsonOptions);

        // 首次提交及规范等价重放都应成功，且只有后者标记为重放。
        var initialBody = await initialResponse.Content.ReadAsStringAsync();
        // 重放响应正文用于确认服务端返回的是规范化后的稳定结果。
        var normalizedReplayBody = await normalizedReplayResponse.Content.ReadAsStringAsync();
        Assert.True(initialResponse.IsSuccessStatusCode, initialBody);
        Assert.True(normalizedReplayResponse.IsSuccessStatusCode, normalizedReplayBody);
        Assert.False(JsonSerializer.Deserialize<RemarkTaskLeaseCompletionResponse>(initialBody, JsonOptions)!.Replayed);
        Assert.True(JsonSerializer.Deserialize<RemarkTaskLeaseCompletionResponse>(normalizedReplayBody, JsonOptions)!.Replayed);
        // 三种完整结果载荷变化都必须稳定返回结果冲突，不能被接受为同一次重放。
        Assert.Equal(HttpStatusCode.Conflict, changedRemarkResponse.StatusCode);
        Assert.Contains("remark_task_result_conflict", await changedRemarkResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, changedFailureResponse.StatusCode);
        Assert.Contains("remark_task_result_conflict", await changedFailureResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, changedStatusResponse.StatusCode);
        Assert.Contains("remark_task_result_conflict", await changedStatusResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证两个完全相同的完成请求并发到达时只写入一次终态，另一个请求作为同结果重放成功返回。
    /// </summary>
    [Fact]
    public async Task Concurrent_identical_completions_converge_to_one_commit_and_one_replay()
    {
        // 独立工厂为并发请求提供同一个真实 SQLite 数据库和不同请求作用域。
        using var factory = new TestApplicationFactory();
        // 管理员客户端创建完整的联系人、权益、规则和备注任务前置条件。
        using var admin = factory.CreateAuthenticatedClient();
        // 同一 Agent 客户端并发提交相同的租约完成凭据。
        using var agent = factory.CreateAgentClient();
        // 待完成任务是两个请求争用的唯一终态资源。
        var task = await CreateReadyTaskAsync(admin);
        // 健康绑定确保两个请求均能通过设备状态门禁。
        var owner = await RegisterHealthyAgentAsync(agent, "concurrent-replay");
        // 单个活动租约为两个并发请求提供相同版本和令牌。
        var claim = await ClaimAsync(agent, owner);
        // 相同结果标识使失败重试能够在首个事务提交后识别为幂等重放。
        var resultId = $"concurrent-same-{Guid.NewGuid():N}";
        // 完整结果载荷在两个请求之间逐字段一致。
        var completion = new RemarkTaskLeaseCompleteRequest(
            owner.InstanceId,
            claim.LeaseToken,
            claim.Version,
            resultId,
            true,
            task.GeneratedRemark,
            null);

        // 两个 HTTP 请求在等待前同时启动，以覆盖事务加锁后的竞争重放分支。
        var pendingResponses = new[]
        {
            agent.PostAsJsonAsync(LeaseRoute(owner, task.Id, "complete"), completion, JsonOptions),
            agent.PostAsJsonAsync(LeaseRoute(owner, task.Id, "complete"), completion, JsonOptions)
        };
        // 等待两次提交完成，防止测试只观察到其中一个请求。
        var responses = await Task.WhenAll(pendingResponses);
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];
        // 响应正文用于反序列化并区分首次提交和幂等重放。
        var responseBodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
        Assert.All(responses, response => Assert.True(response.IsSuccessStatusCode, responseBodies[Array.IndexOf(responses, response)]));
        // 两个成功响应中必须恰好一个标记为首次提交、一个标记为重放。
        var completionResponses = responseBodies
            .Select(body => JsonSerializer.Deserialize<RemarkTaskLeaseCompletionResponse>(body, JsonOptions)!)
            .ToArray();
        Assert.Equal(1, completionResponses.Count(response => !response.Replayed));
        Assert.Equal(1, completionResponses.Count(response => response.Replayed));
        Assert.All(completionResponses, response => Assert.Equal(resultId, response.ResultId));

        // 数据库终态和审计数量用于证明两个成功 HTTP 响应没有造成重复业务写入。
        using var verificationScope = factory.Services.CreateScope();
        // 验证上下文绕过租户过滤器读取最终任务和精确审计条数。
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        // 持久化任务必须只递增一次完成版本并保存唯一结果标识。
        var storedTask = await verificationDb.RemarkTasks.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(item => item.Id == task.Id);
        // 完成审计以任务资源和动作共同筛选，排除创建及权益相关审计。
        var completionAuditCount = await verificationDb.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(item =>
                item.Action == "remark-task.agent-completed" &&
                item.ResourceId == task.Id.ToString("D"));
        Assert.Equal(resultId, storedTask.CompletionResultId);
        Assert.Equal(claim.Version + 1, storedTask.Version);
        Assert.Equal(1, completionAuditCount);
    }

    /// <summary>
    /// 验证同一租约的不同结果标识并发提交时只有一个请求能写入终态，另一个请求必须收到结果冲突。
    /// </summary>
    [Fact]
    public async Task Concurrent_distinct_completions_allow_exactly_one_terminal_result()
    {
        // 独立工厂确保竞争仅发生在本用例创建的单一任务上。
        using var factory = new TestApplicationFactory();
        // 管理员客户端准备有效的自动备注业务上下文。
        using var admin = factory.CreateAuthenticatedClient();
        // Agent 客户端承载两个共享租约但结果标识不同的并发请求。
        using var agent = factory.CreateAgentClient();
        // 单一待处理任务是并发状态转换的目标。
        var task = await CreateReadyTaskAsync(admin);
        // 健康绑定允许请求到达事务竞争点。
        var owner = await RegisterHealthyAgentAsync(agent, "concurrent-conflict");
        // 活动租约为两个竞争请求提供相同的版本和持有证明。
        var claim = await ClaimAsync(agent, owner);
        // 两个不同结果标识代表不可合并的外部执行结论。
        var resultIds = new[]
        {
            $"concurrent-a-{Guid.NewGuid():N}",
            $"concurrent-b-{Guid.NewGuid():N}"
        };
        // 两个请求除结果标识外完全相同，以隔离幂等身份冲突行为。
        var requests = resultIds
            .Select(resultId => new RemarkTaskLeaseCompleteRequest(
                owner.InstanceId,
                claim.LeaseToken,
                claim.Version,
                resultId,
                true,
                task.GeneratedRemark,
                null))
            .ToArray();

        // 同时启动不同结果提交，要求数据库事务串行决定唯一获胜者。
        var pendingResponses = requests
            .Select(request => agent.PostAsJsonAsync(
                LeaseRoute(owner, task.Id, "complete"),
                request,
                JsonOptions))
            .ToArray();
        // 等待所有竞争请求完成后统一断言响应分布。
        var responses = await Task.WhenAll(pendingResponses);
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        // 失败响应必须明确表示结果身份冲突，而不是数据库锁或未处理异常。
        var conflictResponse = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("remark_task_result_conflict", await conflictResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 持久化验证确认只有一个调用方结果进入终态并产生一次完成审计。
        using var verificationScope = factory.Services.CreateScope();
        // 验证上下文读取竞争结束后的稳定数据库状态。
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        // 最终任务只能绑定到两个竞争结果标识之一。
        var storedTask = await verificationDb.RemarkTasks.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(item => item.Id == task.Id);
        // 单条完成审计证明失败请求没有重复执行目标更新或终态写入。
        var completionAuditCount = await verificationDb.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(item =>
                item.Action == "remark-task.agent-completed" &&
                item.ResourceId == task.Id.ToString("D"));
        Assert.Contains(storedTask.CompletionResultId, resultIds);
        Assert.Equal(RemarkTaskStatus.Completed, storedTask.Status);
        Assert.Equal(1, completionAuditCount);
    }

    /// <summary>
    /// 验证失败结果不修改目标备注，并拒绝用同一结果标识提交不同载荷。
    /// </summary>
    [Fact]
    public async Task Failure_completion_preserves_target_and_rejects_result_id_reuse()
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        var firstTask = await CreateReadyTaskAsync(admin);
        var owner = await RegisterHealthyAgentAsync(agent, "complete-failure");
        var claim = await ClaimAsync(agent, owner);
        var resultId = $"failed-{Guid.NewGuid():N}";

        // 首次失败原因带首尾空白，持久化和幂等比较都必须使用裁剪后的规范值。
        using var failed = await agent.PostAsJsonAsync(
            LeaseRoute(owner, firstTask.Id, "complete"),
            new RemarkTaskLeaseCompleteRequest(
                owner.InstanceId,
                claim.LeaseToken,
                claim.Version,
                resultId,
                false,
                null,
                "  UI confirmation unavailable  "),
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);

        // 同一任务、结果标识和规范失败原因属于安全重放，即使原始空白形式不同也应成功。
        using var replayedFailure = await agent.PostAsJsonAsync(
            LeaseRoute(owner, firstTask.Id, "complete"),
            new RemarkTaskLeaseCompleteRequest(
                owner.InstanceId,
                claim.LeaseToken,
                claim.Version,
                resultId,
                false,
                null,
                "UI confirmation unavailable"),
            JsonOptions);
        // 同一任务和结果标识只要失败原因变化，就必须拒绝为结果载荷冲突。
        using var changedFailure = await agent.PostAsJsonAsync(
            LeaseRoute(owner, firstTask.Id, "complete"),
            new RemarkTaskLeaseCompleteRequest(
                owner.InstanceId,
                claim.LeaseToken,
                claim.Version,
                resultId,
                false,
                null,
                "different failure"),
            JsonOptions);
        // 规范等价请求返回重放标志，不得再次写入终态。
        var replayedFailureBody = await replayedFailure.Content.ReadAsStringAsync();
        Assert.True(replayedFailure.IsSuccessStatusCode, replayedFailureBody);
        Assert.True(JsonSerializer.Deserialize<RemarkTaskLeaseCompletionResponse>(replayedFailureBody, JsonOptions)!.Replayed);
        Assert.Equal(HttpStatusCode.Conflict, changedFailure.StatusCode);
        Assert.Contains("remark_task_result_conflict", await changedFailure.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var secondTask = await CreateReadyTaskAsync(admin);
        var secondClaim = await ClaimAsync(agent, owner);
        using var reused = await agent.PostAsJsonAsync(
            LeaseRoute(owner, secondTask.Id, "complete"),
            new RemarkTaskLeaseCompleteRequest(
                owner.InstanceId,
                secondClaim.LeaseToken,
                secondClaim.Version,
                resultId,
                false,
                null,
                "different failure"),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Contains("remark_task_result_conflict", await reused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var contact = await admin.GetFromJsonAsync<ContactItem>(
            $"/api/contacts/{firstTask.TargetId:D}",
            JsonOptions);
        Assert.Null(contact!.SystemRemark);
        Assert.Null(contact.CurrentWeChatRemark);
    }

    /// <summary>
    /// 验证自动化暂停会拒绝新认领，并使已有租约无法完成直到重新建立健康心跳。
    /// </summary>
    [Fact]
    public async Task Automation_pause_closes_claim_and_completion_gates()
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        var task = await CreateReadyTaskAsync(admin);
        var owner = await RegisterHealthyAgentAsync(agent, "pause-gate");
        var claim = await ClaimAsync(agent, owner);
        var state = await admin.GetFromJsonAsync<SystemState>("/api/system-state", JsonOptions);
        using var pause = await admin.PutAsJsonAsync(
            "/api/system-state/automation",
            new AutomationStateRequest(state!.Version, true, "remark lease safety gate"),
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);

        using var completion = await agent.PostAsJsonAsync(
            LeaseRoute(owner, task.Id, "complete"),
            new RemarkTaskLeaseCompleteRequest(
                owner.InstanceId,
                claim.LeaseToken,
                claim.Version,
                $"paused-{Guid.NewGuid():N}",
                true,
                task.GeneratedRemark,
                null),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, completion.StatusCode);
        Assert.Contains("automation_paused", await completion.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证管理员完成接口不能绕过仍有效的 Agent 租约。
    /// </summary>
    [Fact]
    public async Task Administrative_completion_cannot_bypass_an_active_agent_lease()
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        var task = await CreateReadyTaskAsync(admin);
        var owner = await RegisterHealthyAgentAsync(agent, "admin-conflict");
        var claim = await ClaimAsync(agent, owner);

        using var response = await admin.PostAsJsonAsync(
            $"/api/remark-tasks/{task.Id:D}/complete",
            new RemarkTaskCompleteRequest(claim.Version, true, task.GeneratedRemark, null),
            JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("remark_task_leased", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>验证管理员完成接口拒绝成功结果携带失败原因以及失败结果携带已应用备注。</summary>
    /// <param name="succeeded">请求声明的最终结果。</param>
    /// <param name="appliedRemark">请求中的已应用备注。</param>
    /// <param name="failureReason">请求中的失败原因。</param>
    /// <param name="expectedError">期望的稳定错误码。</param>
    [Theory]
    [InlineData(true, "generated-placeholder", "contradictory failure", "failure_reason_not_allowed")]
    [InlineData(false, "contradictory applied remark", "execution failed", "applied_remark_not_allowed")]
    public async Task Administrative_completion_rejects_contradictory_result_fields(
        bool succeeded,
        string? appliedRemark,
        string? failureReason,
        string expectedError)
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        var task = await CreateReadyTaskAsync(admin);
        var normalizedAppliedRemark = succeeded ? task.GeneratedRemark : appliedRemark;

        using var response = await admin.PostAsJsonAsync(
            $"/api/remark-tasks/{task.Id:D}/complete",
            new RemarkTaskCompleteRequest(
                task.Version,
                succeeded,
                normalizedAppliedRemark,
                failureReason),
            JsonOptions);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedError, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证管理员完成在检查无活动租约后仍持有数据库写锁，并发 Agent 只能在管理员提交终态后看到空队列。
    /// </summary>
    [Fact]
    public async Task Administrative_completion_serializes_concurrent_agent_claim()
    {
        var synchronization = new BlockingRemarkTaskMutationSynchronization("remark-task.admin-complete");
        using var factory = new TestApplicationFactory(
            new Dictionary<string, string?>(),
            remarkTaskMutationSynchronization: synchronization);
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        var task = await CreateReadyTaskAsync(admin);
        var owner = await RegisterHealthyAgentAsync(agent, "admin-claim-race");

        var completionTask = admin.PostAsJsonAsync(
            $"/api/remark-tasks/{task.Id:D}/complete",
            new RemarkTaskCompleteRequest(
                task.Version,
                false,
                null,
                "Administrator confirmed execution failure."),
            JsonOptions);
        await synchronization.WaitUntilReachedAsync(TimeSpan.FromSeconds(5));

        var claimStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var claimClient = factory.CreateDefaultClient(new RequestStartedHandler(claimStarted));
        claimClient.DefaultRequestHeaders.Add("X-Api-Key", TestApplicationFactory.AgentApiKey);
        var claimTask = ClaimResponseAsync(claimClient, owner);
        await claimStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertSqliteWriteLockHeldAsync(factory);

        synchronization.Release();
        using var completion = await completionTask;
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        using var claim = await claimTask;
        Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedTask = await verificationDb.RemarkTasks.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(item => item.Id == task.Id);
        Assert.Equal(RemarkTaskStatus.Failed, storedTask.Status);
        Assert.Equal(task.Version + 1, storedTask.Version);
        Assert.Equal(0, storedTask.AttemptCount);
        Assert.Null(storedTask.ClaimedByAgentId);
        Assert.Null(storedTask.LeaseTokenHash);
    }

    /// <summary>验证管理员创建任务在门禁校验后仍持有写锁，并发暂停只能在任务提交后完成。</summary>
    [Fact]
    public async Task Administrative_creation_serializes_concurrent_automation_pause()
    {
        var synchronization = new BlockingRemarkTaskMutationSynchronization(
            "remark-task.admin-create",
            skipMatches: 1);
        using var factory = new TestApplicationFactory(
            new Dictionary<string, string?>(),
            remarkTaskMutationSynchronization: synchronization);
        using var admin = factory.CreateAuthenticatedClient();
        var seed = await CreateReadyTaskAsync(admin);
        var ruleAndTarget = await ResolveRuleAndTargetAsync(factory, seed.Id);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/remark-tasks")
        {
            Content = JsonContent.Create(
                new RemarkTaskRequest(ruleAndTarget.RuleId, ruleAndTarget.TargetId),
                options: JsonOptions)
        };
        createRequest.Headers.Add("Idempotency-Key", $"admin-create-race-{Guid.NewGuid():N}");
        var createTask = admin.SendAsync(createRequest);
        await synchronization.WaitUntilReachedAsync(TimeSpan.FromSeconds(5));

        var state = await admin.GetFromJsonAsync<SystemState>("/api/system-state", JsonOptions);
        var pauseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pauseClient = factory.CreateDefaultClient(new RequestStartedHandler(pauseStarted));
        pauseClient.DefaultRequestHeaders.Add("X-Api-Key", TestApplicationFactory.ApiKey);
        var pauseTask = pauseClient.PutAsJsonAsync(
            "/api/system-state/automation",
            new AutomationStateRequest(state!.Version, true, "serialize remark creation"),
            JsonOptions);
        await pauseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertSqliteWriteLockHeldAsync(factory);

        synchronization.Release();
        using var created = await createTask;
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var paused = await pauseTask;
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
    }

    /// <summary>验证管理员完成遇到持续 SQLite 写锁时返回稳定的可重试冲突。</summary>
    [Fact]
    public async Task Administrative_completion_returns_conflict_when_sqlite_write_lock_persists()
    {
        using var factory = new TestApplicationFactory(
            new Dictionary<string, string?>(),
            databaseDefaultTimeoutSeconds: 1);
        using var admin = factory.CreateAuthenticatedClient();
        var task = await CreateReadyTaskAsync(admin);

        await using var blockerConnection = await OpenWriteLockConnectionAsync(factory);
        await using var blockerTransaction = blockerConnection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: true);
        await AcquireRemarkTaskWriteLockAsync(blockerConnection, blockerTransaction);

        using var response = await admin.PostAsJsonAsync(
            $"/api/remark-tasks/{task.Id:D}/complete",
            new RemarkTaskCompleteRequest(
                task.Version,
                false,
                null,
                "Administrator confirmed execution failure."),
            JsonOptions);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("database_write_busy", body, StringComparison.Ordinal);
        await blockerTransaction.RollbackAsync();
    }

    /// <summary>
    /// 验证管理员接管已过期租约并完成任务时，会清除持有者、实例、令牌摘要和到期时间四项租约状态。
    /// </summary>
    /// <param name="succeeded">管理员提交成功终态时为真，提交失败终态时为假。</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Administrative_completion_clears_every_expired_lease_field(bool succeeded)
    {
        // 独立数据库避免其他租约影响管理员接管场景。
        using var factory = new TestApplicationFactory();
        // 管理员客户端创建任务并在租约过期后提交成功结果。
        using var admin = factory.CreateAuthenticatedClient();
        // Agent 客户端用于先建立一份包含全部租约字段的真实认领记录。
        using var agent = factory.CreateAgentClient();
        // 具备自动备注权益的任务确保管理员完成可以通过业务门禁。
        var task = await CreateReadyTaskAsync(admin);
        // 健康 Agent 绑定是首次领取任务的前置条件。
        var owner = await RegisterHealthyAgentAsync(agent, "admin-expired");
        // 领取结果提供管理员完成必须使用的最新任务版本。
        var claim = await ClaimAsync(agent, owner);

        // 直接推进数据库中的租约到期时间，稳定构造无需真实等待的过期租约。
        using (var expiryScope = factory.Services.CreateScope())
        {
            // 该上下文只修改到期时间，不改变持有者、实例、摘要或版本。
            var expiryDb = expiryScope.ServiceProvider.GetRequiredService<AppDbContext>();
            // 使用显式任务标识约束更新，确保测试不会影响同租户的其他任务。
            var affected = await expiryDb.RemarkTasks.IgnoreQueryFilters()
                .Where(item => item.Id == task.Id)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            Assert.Equal(1, affected);
        }

        // 已过期租约不再阻止管理员完成，但旧租约凭据必须随终态转换一并删除。
        using var completionResponse = await admin.PostAsJsonAsync(
            $"/api/remark-tasks/{task.Id:D}/complete",
            new RemarkTaskCompleteRequest(
                claim.Version,
                succeeded,
                succeeded ? task.GeneratedRemark : null,
                succeeded ? null : "Administrator confirmed execution failure."),
            JsonOptions);
        // 响应正文用于在业务门禁意外失败时输出具体 API 错误。
        var completionBody = await completionResponse.Content.ReadAsStringAsync();
        Assert.True(completionResponse.IsSuccessStatusCode, completionBody);

        // 使用新作用域读取持久化终态，避免被管理员请求作用域的跟踪缓存影响。
        using var verificationScope = factory.Services.CreateScope();
        // 验证上下文绕过租户过滤器读取完整租约字段。
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        // 最终任务必须完成，并且不再保留任何可关联旧 Agent 的租约数据。
        var storedTask = await verificationDb.RemarkTasks.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(item => item.Id == task.Id);
        Assert.Equal(succeeded ? RemarkTaskStatus.Completed : RemarkTaskStatus.Failed, storedTask.Status);
        Assert.Null(storedTask.ClaimedByAgentId);
        Assert.Null(storedTask.ClaimedWeChatInstanceId);
        Assert.Null(storedTask.LeaseTokenHash);
        Assert.Null(storedTask.LeaseExpiresAt);
    }

    /// <summary>
    /// 验证任务创建后目标身份发生变化时，成功完成被拒绝且不覆盖目标备注。
    /// </summary>
    [Fact]
    public async Task Completion_rejects_target_identity_changes_after_task_creation()
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        var task = await CreateReadyTaskAsync(admin);
        var owner = await RegisterHealthyAgentAsync(agent, "identity-change");
        var claim = await ClaimAsync(agent, owner);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var contact = await db.Contacts.IgnoreQueryFilters().SingleAsync(item => item.Id == task.TargetId);
            contact.DisplayName = "Renamed after claim";
            contact.Version++;
            await db.SaveChangesAsync();
        }

        using var response = await agent.PostAsJsonAsync(
            LeaseRoute(owner, task.Id, "complete"),
            new RemarkTaskLeaseCompleteRequest(
                owner.InstanceId,
                claim.LeaseToken,
                claim.Version,
                $"identity-change-{Guid.NewGuid():N}",
                true,
                task.GeneratedRemark,
                null),
            JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("remark_target_identity_changed", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var unchanged = await verificationDb.RemarkTasks.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(item => item.Id == task.Id);
        Assert.Equal(RemarkTaskStatus.Pending, unchanged.Status);
        Assert.Null(unchanged.CompletionResultId);
    }

    /// <summary>
    /// 验证逻辑备份不会序列化活动令牌摘要或租约持有者，恢复出的任务必须重新认领。
    /// </summary>
    [Fact]
    public async Task Backup_payload_strips_active_remark_task_leases()
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        var task = await CreateReadyTaskAsync(admin);
        var owner = await RegisterHealthyAgentAsync(agent, "backup-lease");
        _ = await ClaimAsync(agent, owner);

        using var backupRequest = new HttpRequestMessage(HttpMethod.Post, "/api/backups")
        {
            Content = JsonContent.Create(new CreateBackupRequest("remark lease backup"), options: JsonOptions)
        };
        backupRequest.Headers.Add("Idempotency-Key", $"remark-lease-backup-{Guid.NewGuid():N}");
        using var backupResponse = await admin.SendAsync(backupRequest);
        var backupBody = await backupResponse.Content.ReadAsStringAsync();
        Assert.True(backupResponse.IsSuccessStatusCode, backupBody);
        var backup = JsonSerializer.Deserialize<BackupItem>(backupBody, JsonOptions)!;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.RemarkTasks.IgnoreQueryFilters()
                .Where(item => item.Id == task.Id)
                .ExecuteDeleteAsync();
        }

        using var restoreRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/backups/{backup.Id:D}/restore")
        {
            Content = JsonContent.Create(new RestoreBackupRequest("RESTORE"), options: JsonOptions)
        };
        restoreRequest.Headers.Add("Idempotency-Key", $"remark-lease-restore-{Guid.NewGuid():N}");
        using var restoreResponse = await admin.SendAsync(restoreRequest);
        var restoreBody = await restoreResponse.Content.ReadAsStringAsync();
        Assert.True(restoreResponse.IsSuccessStatusCode, restoreBody);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var restored = await verificationDb.RemarkTasks.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(item => item.Id == task.Id);
        Assert.Equal(1, restored.AttemptCount);
        Assert.Null(restored.ClaimedByAgentId);
        Assert.Null(restored.ClaimedWeChatInstanceId);
        Assert.Null(restored.LeaseTokenHash);
        Assert.Null(restored.LeaseExpiresAt);
    }

    /// <summary>
    /// 验证批量租约写入仍显式受租户条件约束，当前租户不能领取或更新外租户任务。
    /// </summary>
    [Fact]
    public async Task Claim_does_not_cross_the_authenticated_tenant_boundary()
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        var task = await CreateReadyTaskAsync(admin);
        var foreignTenantId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.RemarkTasks.IgnoreQueryFilters().SingleAsync(item => item.Id == task.Id);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE RemarkTasks SET TenantId = {foreignTenantId} WHERE Id = {task.Id}");
            db.Entry(stored).State = EntityState.Detached;
        }
        var owner = await RegisterHealthyAgentAsync(agent, "tenant-boundary");

        using var response = await ClaimResponseAsync(agent, owner);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var foreign = await verificationDb.RemarkTasks.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(item => item.Id == task.Id);
        Assert.Equal(foreignTenantId, foreign.TenantId);
        Assert.Equal(0, foreign.AttemptCount);
        Assert.Null(foreign.LeaseTokenHash);
    }

    /// <summary>
    /// 创建具备 BASIC 自动备注权益的联系人及其待处理任务。
    /// </summary>
    private static async Task<RemarkTaskItem> CreateReadyTaskAsync(HttpClient admin)
    {
        var contactResponse = await admin.PostAsJsonAsync(
            "/api/contacts",
            new ContactCreateRequest(
                $"lease-contact-{Guid.NewGuid():N}",
                "Lease contact",
                $"wx-{Guid.NewGuid():N}",
                $"C-{Guid.NewGuid():N}"[..18],
                null,
                false,
                null),
            JsonOptions);
        var contactBody = await contactResponse.Content.ReadAsStringAsync();
        Assert.True(contactResponse.IsSuccessStatusCode, contactBody);
        var contact = JsonSerializer.Deserialize<ContactItem>(contactBody, JsonOptions)!;

        var codeResponse = await admin.PostAsJsonAsync(
            "/api/activation-codes",
            new IssueActivationCodeRequest("BASIC", ServiceDurationKind.Days30, null),
            JsonOptions);
        var code = await codeResponse.Content.ReadFromJsonAsync<IssuedCode>(JsonOptions);
        using var redeemRequest = new HttpRequestMessage(HttpMethod.Post, "/api/activation-codes/redeem")
        {
            Content = JsonContent.Create(
                new RedeemActivationCodeRequest(code!.Code, ServiceTargetKind.Contact, contact.Id),
                options: JsonOptions)
        };
        redeemRequest.Headers.Add("Idempotency-Key", $"lease-entitlement-{Guid.NewGuid():N}");
        using var redeem = await admin.SendAsync(redeemRequest);
        Assert.True(redeem.IsSuccessStatusCode, await redeem.Content.ReadAsStringAsync());

        var ruleResponse = await admin.PostAsJsonAsync(
            "/api/remark-rules",
            new RemarkRuleCreateRequest(
                $"lease-rule-{Guid.NewGuid():N}",
                ServiceTargetKind.Contact,
                "{customerCode}-{displayName}",
                RemarkConflictPolicy.OverwriteSystemGeneratedOnly,
                true,
                64),
            JsonOptions);
        var ruleBody = await ruleResponse.Content.ReadAsStringAsync();
        Assert.True(ruleResponse.IsSuccessStatusCode, ruleBody);
        var rule = JsonSerializer.Deserialize<RuleItem>(ruleBody, JsonOptions)!;

        using var taskRequest = new HttpRequestMessage(HttpMethod.Post, "/api/remark-tasks")
        {
            Content = JsonContent.Create(new RemarkTaskRequest(rule.Id, contact.Id), options: JsonOptions)
        };
        taskRequest.Headers.Add("Idempotency-Key", $"lease-task-{Guid.NewGuid():N}");
        using var taskResponse = await admin.SendAsync(taskRequest);
        var taskBody = await taskResponse.Content.ReadAsStringAsync();
        Assert.True(taskResponse.IsSuccessStatusCode, taskBody);
        return JsonSerializer.Deserialize<RemarkTaskItem>(taskBody, JsonOptions)!;
    }

    /// <summary>
    /// 使用 Agent Key 建立最近健康的 dry-run 心跳绑定。
    /// </summary>
    private static async Task<AgentIdentity> RegisterHealthyAgentAsync(HttpClient client, string prefix)
    {
        var identity = new AgentIdentity(
            $"{prefix}-{Guid.NewGuid():N}",
            $"wx-{prefix}-{Guid.NewGuid():N}");
        var sentAt = DateTimeOffset.UtcNow;
        using var response = await client.PostAsJsonAsync(
            "/api/agents/heartbeat",
            new AgentHeartbeatRequest(
                identity.AgentId,
                identity.InstanceId,
                sentAt,
                new AgentRuntimeSnapshotRequest(
                    AgentOperatingState.Healthy,
                    "HEALTHY",
                    "Lease integration test heartbeat.",
                    sentAt,
                    null,
                    null),
                0,
                0,
                true,
                "1.0.0-lease-test"),
            JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.True(JsonSerializer.Deserialize<AgentHeartbeatResponse>(body, JsonOptions)!.Accepted);
        return identity;
    }

    /// <summary>向指定 Agent 的领取端点发送请求并保留原始响应。</summary>
    private static Task<HttpResponseMessage> ClaimResponseAsync(HttpClient client, AgentIdentity identity) =>
        client.PostAsJsonAsync(
            $"/api/agents/{identity.AgentId}/remark-tasks/claim",
            new RemarkTaskClaimRequest(identity.InstanceId),
            JsonOptions);

    /// <summary>领取任务并反序列化成功响应。</summary>
    private static async Task<RemarkTaskLeaseResponse> ClaimAsync(HttpClient client, AgentIdentity identity)
    {
        using var response = await ClaimResponseAsync(client, identity);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NoContent, body);
        return JsonSerializer.Deserialize<RemarkTaskLeaseResponse>(body, JsonOptions)!;
    }

    /// <summary>读取既有测试任务的规则和目标标识，供并发创建回归复用相同有效权益。</summary>
    private static async Task<(Guid RuleId, Guid TargetId)> ResolveRuleAndTargetAsync(
        TestApplicationFactory factory,
        Guid taskId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var task = await db.RemarkTasks.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(item => item.Id == taskId);
        return (task.RuleId, task.TargetId);
    }

    /// <summary>打开不使用连接池且采用短锁等待的独立 SQLite 连接。</summary>
    private static async Task<SqliteConnection> OpenWriteLockConnectionAsync(TestApplicationFactory factory)
    {
        var connection = new SqliteConnection(
            $"Data Source={factory.DatabasePath};Default Timeout=1;Pooling=False");
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>在指定事务中取得备注任务表对应的 SQLite 数据库写锁。</summary>
    private static async Task AcquireRemarkTaskWriteLockAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE RemarkTasks SET Version = Version WHERE 0 = 1;";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>使用独立连接确认目标请求在同步点仍持有 SQLite 数据库写锁。</summary>
    private static async Task AssertSqliteWriteLockHeldAsync(TestApplicationFactory factory)
    {
        await using var connection = await OpenWriteLockConnectionAsync(factory);
        var exception = Assert.Throws<SqliteException>(() =>
            connection.BeginTransaction(IsolationLevel.Serializable, deferred: false));
        Assert.Contains(exception.SqliteErrorCode, new[] { 5, 6 });
    }

    /// <summary>生成指定 Agent 和任务的租约操作路由。</summary>
    private static string LeaseRoute(AgentIdentity identity, Guid taskId, string operation) =>
        $"/api/agents/{identity.AgentId}/remark-tasks/{taskId:D}/{operation}";

    /// <summary>表示测试中的 Agent 与微信实例绑定。</summary>
    private sealed record AgentIdentity(string AgentId, string InstanceId);

    /// <summary>表示激活码签发响应中本测试需要的字段。</summary>
    private sealed record IssuedCode(string Code);

    /// <summary>表示备注规则响应中本测试需要的字段。</summary>
    private sealed record RuleItem(Guid Id);

    /// <summary>表示联系人响应中本测试需要的字段。</summary>
    private sealed record ContactItem(Guid Id, string? SystemRemark, string? CurrentWeChatRemark);

    /// <summary>表示系统自动化状态响应中本测试需要的字段。</summary>
    private sealed record SystemState(long Version);

    /// <summary>表示备份创建响应中本测试需要的字段。</summary>
    private sealed record BackupItem(Guid Id);

    /// <summary>表示备注任务响应中本测试需要的字段。</summary>
    private sealed record RemarkTaskItem(
        Guid Id,
        Guid TargetId,
        string GeneratedRemark,
        RemarkTaskStatus Status,
        long Version);
}
