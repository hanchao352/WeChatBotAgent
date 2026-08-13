using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;

namespace WeChatBot.Backend.Tests;

/// <summary>
/// 验证 Agent 控制面凭据必须与具体注册记录绑定，不能仅凭共享密钥声明任意业务身份。
/// </summary>
public sealed class AgentCredentialIntegrationTests
{
    /// <summary>按 Web API 约定序列化枚举，确保测试请求与真实 Agent 请求格式一致。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// 验证 Agent 群提及上报遇到持续 SQLite 写锁时返回稳定 409，而不是泄漏驱动异常或 HTTP 500。
    /// </summary>
    [Fact]
    public async Task Group_mention_returns_conflict_when_sqlite_write_lock_persists()
    {
        using var factory = new TestApplicationFactory(
            new Dictionary<string, string?>(),
            databaseDefaultTimeoutSeconds: 1);
        using var admin = factory.CreateAuthenticatedClient();
        var issued = await RegisterAsync(
            admin,
            $"mention-busy-{Guid.NewGuid():N}",
            $"wx-{Guid.NewGuid():N}");
        var group = await CreateGroupAsync(admin);
        using var agent = CreateAgentClient(factory, issued.Credential);

        using (var heartbeat = await agent.PostAsJsonAsync(
                   "/api/agents/heartbeat",
                   CreateHeartbeat(issued.Agent.AgentId, issued.Agent.WeChatInstanceId),
                   JsonOptions))
        {
            Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        }

        var connectionString = $"Data Source={factory.DatabasePath};Default Timeout=1;Pooling=False";
        await using var blockerConnection = new SqliteConnection(connectionString);
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = blockerConnection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: true);
        await using (var lockCommand = blockerConnection.CreateCommand())
        {
            lockCommand.Transaction = blockerTransaction;
            lockCommand.CommandText = "UPDATE GroupMentions SET CapturedAt = CapturedAt WHERE 0 = 1;";
            await lockCommand.ExecuteNonQueryAsync();
        }

        using var response = await agent.PostAsJsonAsync(
            $"/api/agents/{issued.Agent.AgentId}/group-mentions",
            new AgentGroupMentionRequest(
                issued.Agent.WeChatInstanceId,
                new GroupMentionRequest(
                    $"busy-event-{Guid.NewGuid():N}",
                    group.Id,
                    "sender-external-id",
                    "@bot busy mapping",
                    true,
                    false,
                    DateTimeOffset.UtcNow)),
            JsonOptions);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("database_write_busy", body, StringComparison.Ordinal);
        await blockerTransaction.RollbackAsync();
    }

    /// <summary>
    /// 复现旧共享 AgentApiKey 的身份冒用缺陷：同一密钥不能通过替换正文中的 AgentId 冒用另一注册。
    /// </summary>
    [Fact]
    public async Task Shared_agent_key_cannot_impersonate_another_registered_agent()
    {
        // 显式关闭旧共享密钥兼容模式；修复前实现忽略该门禁，仍会把共享密钥认证为 Agent。
        using var factory = new TestApplicationFactory(new Dictionary<string, string?>
        {
            ["Auth:AllowLegacySharedAgentApiKey"] = "false",
            ["Auth:AllowAgentAutoRegistration"] = "false"
        });
        using var admin = factory.CreateAuthenticatedClient();
        using var legacyAgent = factory.CreateAgentClient();
        var firstAgentId = $"credential-owner-{Guid.NewGuid():N}";
        var secondAgentId = $"credential-target-{Guid.NewGuid():N}";

        // 两个注册拥有不同微信实例；旧实现却无法从共享密钥判断实际调用者属于哪一个注册。
        _ = await RegisterAsync(admin, firstAgentId, $"wx-{Guid.NewGuid():N}");
        var secondInstanceId = $"wx-{Guid.NewGuid():N}";
        _ = await RegisterAsync(admin, secondAgentId, secondInstanceId);

        using var response = await legacyAgent.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat(secondAgentId, secondInstanceId),
            JsonOptions);

        // 安全实现必须在认证入口拒绝未启用的共享密钥；修复前该请求错误返回 200。
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// 以可执行证据记录兼容共享 Key 的固有限制：任一持有者都能声明任一已知注册，故该模式不得用于生产。
    /// </summary>
    [Fact]
    public async Task Legacy_shared_key_can_assume_any_known_registration_and_is_nonproduction_only()
    {
        using var factory = new TestApplicationFactory(new Dictionary<string, string?>
        {
            ["Auth:AllowLegacySharedAgentApiKey"] = "true",
            ["Auth:AllowAgentAutoRegistration"] = "false"
        });
        using var admin = factory.CreateAuthenticatedClient();
        var first = await RegisterAsync(
            admin,
            $"legacy-owner-a-{Guid.NewGuid():N}",
            $"wx-legacy-a-{Guid.NewGuid():N}");
        var second = await RegisterAsync(
            admin,
            $"legacy-owner-b-{Guid.NewGuid():N}",
            $"wx-legacy-b-{Guid.NewGuid():N}");
        using var sharedCredentialClient = factory.CreateAgentClient();

        // 同一个共享密钥可分别提交两个注册的完整公开身份字段，服务端无法判断真实设备归属。
        using var firstResponse = await sharedCredentialClient.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat(first.Agent.AgentId, first.Agent.WeChatInstanceId),
            JsonOptions);
        using var secondResponse = await sharedCredentialClient.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat(second.Agent.AgentId, second.Agent.WeChatInstanceId),
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.True((await firstResponse.Content.ReadFromJsonAsync<AgentHeartbeatResponse>(JsonOptions))!.Accepted);
        Assert.True((await secondResponse.Content.ReadFromJsonAsync<AgentHeartbeatResponse>(JsonOptions))!.Accepted);
    }

    /// <summary>验证首次签发仅返回一次明文，列表与数据库永远只暴露非敏感状态或摘要。</summary>
    [Fact]
    public async Task Registration_returns_credential_once_and_never_lists_or_stores_plaintext()
    {
        using var factory = CreateIndependentCredentialFactory();
        using var admin = factory.CreateAuthenticatedClient();
        var agentId = $"single-issue-{Guid.NewGuid():N}";
        var instanceId = $"wx-{Guid.NewGuid():N}";

        var issued = await RegisterAsync(admin, agentId, instanceId);
        Assert.StartsWith("wba_", issued.Credential, StringComparison.Ordinal);
        Assert.True(issued.Credential.Length >= 40);
        Assert.True(issued.Agent.HasCredential);

        using var duplicate = await admin.PostAsJsonAsync(
            "/api/agents",
            new RegisterAgentRequest(agentId, instanceId, "credential-test-1"),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.DoesNotContain(issued.Credential, await duplicate.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var listBody = await admin.GetStringAsync("/api/agents");
        Assert.DoesNotContain(issued.Credential, listBody, StringComparison.Ordinal);
        Assert.DoesNotContain("credentialHash", listBody, StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registration = await db.AgentRegistrations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == issued.Agent.Id);
        Assert.NotEqual(issued.Credential, registration.CredentialHash);
        Assert.Equal(64, registration.CredentialHash!.Length);
    }

    /// <summary>验证每个独立凭据只能声明其数据库绑定的 AgentId 和微信实例。</summary>
    [Fact]
    public async Task Independent_credential_rejects_cross_agent_and_cross_instance_heartbeat()
    {
        using var factory = CreateIndependentCredentialFactory();
        using var admin = factory.CreateAuthenticatedClient();
        var first = await RegisterAsync(
            admin,
            $"bound-a-{Guid.NewGuid():N}",
            $"wx-a-{Guid.NewGuid():N}");
        var second = await RegisterAsync(
            admin,
            $"bound-b-{Guid.NewGuid():N}",
            $"wx-b-{Guid.NewGuid():N}");
        using var firstClient = CreateAgentClient(factory, first.Credential);

        using var crossAgent = await firstClient.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat(second.Agent.AgentId, second.Agent.WeChatInstanceId),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, crossAgent.StatusCode);

        using var crossInstance = await firstClient.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat(first.Agent.AgentId, second.Agent.WeChatInstanceId),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, crossInstance.StatusCode);

        using var valid = await firstClient.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat(first.Agent.AgentId, first.Agent.WeChatInstanceId),
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    /// <summary>验证认证摘要查询显式限定当前租户，不能使用外租户注册的有效凭据。</summary>
    [Fact]
    public async Task Independent_credential_cannot_cross_tenant_boundary()
    {
        using var factory = CreateIndependentCredentialFactory();
        using var admin = factory.CreateAuthenticatedClient();
        _ = await admin.GetAsync("/api/agents");
        var foreignCredential = AgentCredentialSecurity.CreateCredential();
        var foreignAgentId = $"foreign-{Guid.NewGuid():N}";
        var foreignInstanceId = $"wx-foreign-{Guid.NewGuid():N}";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            // 生产 DbContext 正确禁止应用层写入外租户；测试用参数化 SQL 模拟数据库中已存在的其他租户注册。
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO AgentRegistrations
                    (Id, TenantId, AgentId, NormalizedAgentId, WeChatInstanceId, IsEnabled,
                     ConfigurationVersion, CredentialHash, CredentialIssuedAt, CredentialRotatedAt,
                     CredentialRevokedAt, RegisteredAt, UpdatedAt, Version)
                VALUES
                    ({Guid.NewGuid()}, {Guid.NewGuid()}, {foreignAgentId}, {foreignAgentId.ToUpperInvariant()},
                     {foreignInstanceId}, {true}, {"foreign-1"},
                     {AgentCredentialSecurity.HashCredential(foreignCredential)}, {now}, {null}, {null},
                     {now}, {now}, {1})
                """);
        }

        using var foreignClient = CreateAgentClient(factory, foreignCredential);
        using var response = await foreignClient.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat(foreignAgentId, foreignInstanceId),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>验证轮换立即使旧凭据和旧在线会话失效、并发版本拒绝重放，随后吊销新凭据。</summary>
    [Fact]
    public async Task Rotation_and_revocation_invalidate_previous_credentials_with_version_control()
    {
        using var factory = CreateIndependentCredentialFactory();
        using var admin = factory.CreateAuthenticatedClient();
        var issued = await RegisterAsync(
            admin,
            $"lifecycle-{Guid.NewGuid():N}",
            $"wx-{Guid.NewGuid():N}");

        var rotateTasks = Enumerable.Range(0, 2)
            .Select(_ => admin.PostAsJsonAsync(
                $"/api/agents/{issued.Agent.Id:D}/credential/rotate",
                new AgentCredentialVersionRequest(issued.Agent.Version),
                JsonOptions))
            .ToArray();
        var rotateResponses = await Task.WhenAll(rotateTasks);
        var successfulRotation = Assert.Single(rotateResponses, response => response.IsSuccessStatusCode);
        var failedRotation = Assert.Single(rotateResponses, response => !response.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, failedRotation.StatusCode);
        var rotated = await successfulRotation.Content.ReadFromJsonAsync<AgentCredentialIssueResponse>(JsonOptions);
        Assert.NotNull(rotated);
        Assert.NotEqual(issued.Credential, rotated.Credential);

        var afterRotation = await admin.GetFromJsonAsync<List<AgentListItem>>("/api/agents", JsonOptions);
        var rotatedListItem = Assert.Single(afterRotation!, agent => agent.Id == rotated.Agent.Id);
        Assert.False(rotatedListItem.Online);

        using var oldClient = CreateAgentClient(factory, issued.Credential);
        using var oldResponse = await oldClient.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat(issued.Agent.AgentId, issued.Agent.WeChatInstanceId),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, oldResponse.StatusCode);

        using var newClient = CreateAgentClient(factory, rotated.Credential);
        using var accepted = await newClient.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat(rotated.Agent.AgentId, rotated.Agent.WeChatInstanceId),
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var afterNewHeartbeat = await admin.GetFromJsonAsync<List<AgentListItem>>("/api/agents", JsonOptions);
        var onlineListItem = Assert.Single(afterNewHeartbeat!, agent => agent.Id == rotated.Agent.Id);
        Assert.True(onlineListItem.Online);

        using var revoke = await admin.PostAsJsonAsync(
            $"/api/agents/{rotated.Agent.Id:D}/credential/revoke",
            new AgentCredentialVersionRequest(rotated.Agent.Version),
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var revoked = await revoke.Content.ReadFromJsonAsync<AgentListItem>(JsonOptions);
        Assert.NotNull(revoked);
        Assert.False(revoked.HasCredential);
        Assert.NotNull(revoked.CredentialRevokedAt);
        Assert.False(revoked.Online);

        using var revokedResponse = await newClient.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat(rotated.Agent.AgentId, rotated.Agent.WeChatInstanceId),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedResponse.StatusCode);

        foreach (var response in rotateResponses) response.Dispose();
    }

    /// <summary>验证群消息与全部备注租约入口在业务逻辑前拒绝冒用的路由或实例身份。</summary>
    [Fact]
    public async Task Agent_business_endpoints_enforce_credential_route_and_instance_binding()
    {
        using var factory = CreateIndependentCredentialFactory();
        using var admin = factory.CreateAuthenticatedClient();
        var owner = await RegisterAsync(
            admin,
            $"endpoint-owner-{Guid.NewGuid():N}",
            $"wx-owner-{Guid.NewGuid():N}");
        var target = await RegisterAsync(
            admin,
            $"endpoint-target-{Guid.NewGuid():N}",
            $"wx-target-{Guid.NewGuid():N}");
        using var ownerClient = CreateAgentClient(factory, owner.Credential);
        using var heartbeat = await ownerClient.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat(owner.Agent.AgentId, owner.Agent.WeChatInstanceId),
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);

        using var groupUpload = await ownerClient.PostAsJsonAsync(
            $"/api/agents/{target.Agent.AgentId}/group-mentions",
            new AgentGroupMentionRequest(
                target.Agent.WeChatInstanceId,
                new GroupMentionRequest(
                    $"credential-event-{Guid.NewGuid():N}",
                    Guid.NewGuid(),
                    "sender",
                    "message",
                    false,
                    false,
                    DateTimeOffset.UtcNow)),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, groupUpload.StatusCode);

        var leaseToken = new string('x', 32);
        var operations = new (string Path, object Body)[]
        {
            (
                $"/api/agents/{target.Agent.AgentId}/remark-tasks/claim",
                new RemarkTaskClaimRequest(target.Agent.WeChatInstanceId)),
            (
                $"/api/agents/{target.Agent.AgentId}/remark-tasks/{Guid.NewGuid():D}/renew",
                new RemarkTaskLeaseRequest(target.Agent.WeChatInstanceId, leaseToken, 1)),
            (
                $"/api/agents/{target.Agent.AgentId}/remark-tasks/{Guid.NewGuid():D}/release",
                new RemarkTaskLeaseRequest(target.Agent.WeChatInstanceId, leaseToken, 1)),
            (
                $"/api/agents/{target.Agent.AgentId}/remark-tasks/{Guid.NewGuid():D}/complete",
                new RemarkTaskLeaseCompleteRequest(
                    target.Agent.WeChatInstanceId,
                    leaseToken,
                    1,
                    "credential-binding-result",
                    false,
                    null,
                    "credential binding test"))
        };
        foreach (var (path, body) in operations)
        {
            // 统一绑定校验发生在任务读取和租约令牌校验之前，因此最小 JSON 即可证明入口身份约束。
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body, options: JsonOptions)
            };
            using var response = await ownerClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// 验证 Agent 状态转换持有写锁期间，凭据轮换必须等待业务事务提交，不能插入身份复核和写入之间。
    /// </summary>
    [Fact]
    public async Task Credential_rotation_waits_for_claim_transaction_after_binding_validation()
    {
        var synchronization = new BlockingAgentMutationSynchronization("remark-task.claim");
        using var factory = CreateIndependentCredentialFactory(synchronization);
        using var admin = factory.CreateAuthenticatedClient();
        var issued = await RegisterAsync(
            admin,
            $"claim-race-{Guid.NewGuid():N}",
            $"wx-{Guid.NewGuid():N}");
        using var agent = CreateAgentClient(factory, issued.Credential);

        using (var heartbeat = await agent.PostAsJsonAsync(
                   "/api/agents/heartbeat",
                   CreateHeartbeat(issued.Agent.AgentId, issued.Agent.WeChatInstanceId),
                   JsonOptions))
        {
            Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        }

        var claimTask = agent.PostAsJsonAsync(
            $"/api/agents/{issued.Agent.AgentId}/remark-tasks/claim",
            new RemarkTaskClaimRequest(issued.Agent.WeChatInstanceId),
            JsonOptions);
        await synchronization.WaitUntilReachedAsync(TimeSpan.FromSeconds(5));

        var rotationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rotationClient = factory.CreateDefaultClient(new RequestStartedHandler(rotationStarted));
        rotationClient.DefaultRequestHeaders.Add("X-Api-Key", TestApplicationFactory.ApiKey);
        var rotationTask = rotationClient.PostAsJsonAsync(
            $"/api/agents/{issued.Agent.Id:D}/credential/rotate",
            new AgentCredentialVersionRequest(issued.Agent.Version),
            JsonOptions);
        await rotationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // 请求已经交给 TestServer；在锁探针返回 SQLITE_BUSY/LOCKED 前，首事务仍保持确定性同步点。
        await AssertSqliteWriteLockHeldAsync(factory);

        synchronization.Release();
        using var claim = await claimTask;
        Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);
        using var rotation = await rotationTask;
        Assert.Equal(HttpStatusCode.OK, rotation.StatusCode);

        // 事务提交后旧凭据再次请求心跳必须在认证层被拒绝，证明轮换没有仅停留在内存状态。
        using var oldCredentialHeartbeat = await agent.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat(issued.Agent.AgentId, issued.Agent.WeChatInstanceId),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, oldCredentialHeartbeat.StatusCode);
    }

    /// <summary>
    /// 验证群提及写入在身份复核后持有写锁，吊销不能插入写入窗口，提交后旧凭据立即失效。
    /// </summary>
    [Fact]
    public async Task Credential_revocation_waits_for_group_mention_transaction_after_binding_validation()
    {
        var synchronization = new BlockingAgentMutationSynchronization("group-mention.ingest");
        using var factory = CreateIndependentCredentialFactory(synchronization);
        using var admin = factory.CreateAuthenticatedClient();
        var issued = await RegisterAsync(
            admin,
            $"mention-race-{Guid.NewGuid():N}",
            $"wx-{Guid.NewGuid():N}");
        var group = await CreateGroupAsync(admin);
        using var agent = CreateAgentClient(factory, issued.Credential);

        using (var heartbeat = await agent.PostAsJsonAsync(
                   "/api/agents/heartbeat",
                   CreateHeartbeat(issued.Agent.AgentId, issued.Agent.WeChatInstanceId),
                   JsonOptions))
        {
            Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        }

        var mentionTask = agent.PostAsJsonAsync(
            $"/api/agents/{issued.Agent.AgentId}/group-mentions",
            new AgentGroupMentionRequest(
                issued.Agent.WeChatInstanceId,
                new GroupMentionRequest(
                    $"race-event-{Guid.NewGuid():N}",
                    group.Id,
                    "sender-external-id",
                    "@bot deterministic race",
                    true,
                    false,
                    DateTimeOffset.UtcNow)),
            JsonOptions);
        await synchronization.WaitUntilReachedAsync(TimeSpan.FromSeconds(5));

        var revokeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var revokeClient = factory.CreateDefaultClient(new RequestStartedHandler(revokeStarted));
        revokeClient.DefaultRequestHeaders.Add("X-Api-Key", TestApplicationFactory.ApiKey);
        var revokeTask = revokeClient.PostAsJsonAsync(
            $"/api/agents/{issued.Agent.Id:D}/credential/revoke",
            new AgentCredentialVersionRequest(issued.Agent.Version),
            JsonOptions);
        await revokeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertSqliteWriteLockHeldAsync(factory);
        Assert.False(revokeTask.IsCompleted);

        synchronization.Release();
        using var mention = await mentionTask;
        Assert.Equal(HttpStatusCode.Created, mention.StatusCode);
        using var revoke = await revokeTask;
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        // 吊销事务已提交，原明文凭据必须无法重新认证。
        using var oldCredentialHeartbeat = await agent.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat(issued.Agent.AgentId, issued.Agent.WeChatInstanceId),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, oldCredentialHeartbeat.StatusCode);
    }

    /// <summary>使用第二 SQLite 连接确认认领事务仍持有数据库写锁。</summary>
    private static async Task AssertSqliteWriteLockHeldAsync(TestApplicationFactory factory)
    {
        // 使用短但有限的数据库等待，避免锁探针永久挂起；确定性来自同步闸门而非该超时。
        var connectionString = $"Data Source={factory.DatabasePath};Default Timeout=1;Pooling=False";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var exception = Assert.Throws<SqliteException>(() =>
            connection.BeginTransaction(IsolationLevel.Serializable, deferred: false));
        Assert.Contains(exception.SqliteErrorCode, new[] { 5, 6 });
    }

    /// <summary>验证逻辑备份不含凭据材料，且恢复后现存和恢复注册的旧凭据均失效。</summary>
    [Fact]
    public async Task Backup_excludes_credentials_and_restore_requires_reissue()
    {
        using var factory = CreateIndependentCredentialFactory();
        using var admin = factory.CreateAuthenticatedClient();
        var issued = await RegisterAsync(
            admin,
            $"backup-credential-{Guid.NewGuid():N}",
            $"wx-{Guid.NewGuid():N}");
        using (var credentialClient = CreateAgentClient(factory, issued.Credential))
        {
            // 先建立真实在线状态，才能证明恢复后保留遥测但凭据门禁会立即使设备离线。
            using var heartbeat = await credentialClient.PostAsJsonAsync(
                "/api/agents/heartbeat",
                CreateHeartbeat(issued.Agent.AgentId, issued.Agent.WeChatInstanceId),
                JsonOptions);
            Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        }

        using var backupRequest = new HttpRequestMessage(HttpMethod.Post, "/api/backups")
        {
            Content = JsonContent.Create(new CreateBackupRequest("credential backup"), options: JsonOptions)
        };
        backupRequest.Headers.Add("Idempotency-Key", $"credential-backup-{Guid.NewGuid():N}");
        using var backupResponse = await admin.SendAsync(backupRequest);
        var backupBody = await backupResponse.Content.ReadAsStringAsync();
        Assert.True(backupResponse.IsSuccessStatusCode, backupBody);
        var backup = JsonSerializer.Deserialize<BackupItem>(backupBody, JsonOptions)!;

        byte[] plaintext;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var manifest = await db.BackupManifests.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == backup.Id);
            var encrypted = await File.ReadAllBytesAsync(Path.Combine(factory.BackupDirectory, manifest.FileName));
            plaintext = DecryptBackup(
                encrypted,
                SHA256.HashData("integration-test-backup-key"u8.ToArray()));
        }
        var backupJson = System.Text.Encoding.UTF8.GetString(plaintext);
        Assert.DoesNotContain(issued.Credential, backupJson, StringComparison.Ordinal);
        Assert.DoesNotContain("credentialHash", backupJson, StringComparison.OrdinalIgnoreCase);

        using var restoreRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/backups/{backup.Id:D}/restore")
        {
            Content = JsonContent.Create(new RestoreBackupRequest("RESTORE"), options: JsonOptions)
        };
        restoreRequest.Headers.Add("Idempotency-Key", $"credential-restore-{Guid.NewGuid():N}");
        using var restore = await admin.SendAsync(restoreRequest);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.AgentHeartbeatStates.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(state => state.AgentRegistrationId == issued.Agent.Id));
        }

        using var oldClient = CreateAgentClient(factory, issued.Credential);
        using var oldCredentialResponse = await oldClient.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat(issued.Agent.AgentId, issued.Agent.WeChatInstanceId),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, oldCredentialResponse.StatusCode);
    }

    /// <summary>
    /// 验证 schema v3 旧备份和 schema v4 租约备份恢复 AgentRegistration 时都不会继承灾备前凭据。
    /// </summary>
    /// <param name="schemaVersion">待模拟的受支持历史备份版本。</param>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Historical_backup_restore_clears_agent_credentials(int schemaVersion)
    {
        using var factory = CreateIndependentCredentialFactory();
        using var admin = factory.CreateAuthenticatedClient();
        var issued = await RegisterAsync(
            admin,
            $"historical-{schemaVersion}-{Guid.NewGuid():N}",
            $"wx-{Guid.NewGuid():N}");
        var backup = await CreateBackupAsync(admin, $"historical credential schema {schemaVersion}");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var registration = await db.AgentRegistrations.IgnoreQueryFilters()
                .SingleAsync(x => x.Id == issued.Agent.Id);
            db.AgentRegistrations.Remove(registration);
            await db.SaveChangesAsync();
        }
        await RewriteBackupSchemaAsync(factory, backup.Id, schemaVersion);

        using var restoreRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/backups/{backup.Id:D}/restore")
        {
            Content = JsonContent.Create(new RestoreBackupRequest("RESTORE"), options: JsonOptions)
        };
        restoreRequest.Headers.Add(
            "Idempotency-Key",
            $"historical-credential-restore-{schemaVersion}-{Guid.NewGuid():N}");
        using var restore = await admin.SendAsync(restoreRequest);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var restored = await db.AgentRegistrations.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == issued.Agent.Id);
            Assert.Null(restored.CredentialHash);
            Assert.NotNull(restored.CredentialRevokedAt);
        }
        using var oldClient = CreateAgentClient(factory, issued.Credential);
        using var oldCredentialResponse = await oldClient.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat(issued.Agent.AgentId, issued.Agent.WeChatInstanceId),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, oldCredentialResponse.StatusCode);
    }

    /// <summary>创建一条管理员预注册记录并验证服务端确实接受。</summary>
    private static async Task<AgentCredentialIssueResponse> RegisterAsync(
        HttpClient admin,
        string agentId,
        string instanceId)
    {
        using var response = await admin.PostAsJsonAsync(
            "/api/agents",
            new RegisterAgentRequest(agentId, instanceId, "credential-test-1"),
            JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<AgentCredentialIssueResponse>(body, JsonOptions)!;
    }

    /// <summary>创建群提及竞态测试使用的最小群记录。</summary>
    private static async Task<GroupItem> CreateGroupAsync(HttpClient admin)
    {
        using var response = await admin.PostAsJsonAsync(
            "/api/groups",
            new GroupCreateRequest(
                $"race-group-{Guid.NewGuid():N}",
                "Credential race group",
                null,
                null,
                false,
                null),
            JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<GroupItem>(body, JsonOptions)!;
    }

    /// <summary>创建关闭共享凭据和自动注册的独立凭据测试宿主。</summary>
    /// <param name="synchronization">可选的事务同步探针，仅用于稳定复现并发窗口。</param>
    private static TestApplicationFactory CreateIndependentCredentialFactory(
        IAgentMutationSynchronization? synchronization = null) =>
        new(
            new Dictionary<string, string?>
            {
                ["Auth:AllowLegacySharedAgentApiKey"] = "false",
                ["Auth:AllowAgentAutoRegistration"] = "false"
            },
            synchronization);

    /// <summary>创建只携带指定独立凭据的 HTTP 客户端。</summary>
    private static HttpClient CreateAgentClient(TestApplicationFactory factory, string credential)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", credential);
        return client;
    }

    /// <summary>创建一份逻辑备份并返回其清单标识。</summary>
    private static async Task<BackupItem> CreateBackupAsync(HttpClient admin, string reason)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/backups")
        {
            Content = JsonContent.Create(new CreateBackupRequest(reason), options: JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", $"agent-credential-backup-{Guid.NewGuid():N}");
        using var response = await admin.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<BackupItem>(body, JsonOptions)!;
    }

    /// <summary>
    /// 将测试备份的载荷与清单版本降级为受支持历史版本，同时重新加密并更新完整性摘要。
    /// </summary>
    private static async Task RewriteBackupSchemaAsync(
        TestApplicationFactory factory,
        Guid backupId,
        int schemaVersion)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manifest = await db.BackupManifests.IgnoreQueryFilters()
            .SingleAsync(x => x.Id == backupId);
        var path = Path.Combine(factory.BackupDirectory, manifest.FileName);
        var key = SHA256.HashData("integration-test-backup-key"u8.ToArray());
        var plaintext = DecryptBackup(await File.ReadAllBytesAsync(path), key);
        using var document = JsonDocument.Parse(plaintext);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.NameEquals("schemaVersion"))
                {
                    writer.WriteNumberValue(schemaVersion);
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        var encrypted = EncryptBackup(output.ToArray(), key);
        await File.WriteAllBytesAsync(path, encrypted);
        manifest.SchemaVersion = schemaVersion;
        manifest.PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(encrypted));
        manifest.Bytes = encrypted.LongLength;
        await db.SaveChangesAsync();
    }

    /// <summary>创建时间戳有效且保持强制 dry-run 的最小心跳请求。</summary>
    private static AgentHeartbeatRequest CreateHeartbeat(string agentId, string instanceId)
    {
        var sentAt = DateTimeOffset.UtcNow;
        return new AgentHeartbeatRequest(
            agentId,
            instanceId,
            sentAt,
            new AgentRuntimeSnapshotRequest(
                AgentOperatingState.Healthy,
                "HEALTHY",
                "Credential binding regression test.",
                sentAt,
                null,
                null),
            0,
            0,
            true,
            "1.0.0-test");
    }

    /// <summary>解密测试工厂创建的 AES-256-GCM 逻辑备份，仅用于断言序列化字段。</summary>
    private static byte[] DecryptBackup(byte[] encrypted, byte[] key)
    {
        ReadOnlySpan<byte> magic = "WXB1"u8;
        const int nonceLength = 12;
        const int authenticationTagLength = 16;
        Assert.True(encrypted.AsSpan(0, magic.Length).SequenceEqual(magic));
        var nonce = encrypted.AsSpan(magic.Length, nonceLength);
        var authenticationTag = encrypted.AsSpan(magic.Length + nonceLength, authenticationTagLength);
        var ciphertext = encrypted.AsSpan(magic.Length + nonceLength + authenticationTagLength);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, authenticationTagLength);
        aes.Decrypt(nonce, ciphertext, authenticationTag, plaintext, magic);
        return plaintext;
    }

    /// <summary>使用与生产备份一致的 AES-256-GCM 信封重新加密测试载荷。</summary>
    private static byte[] EncryptBackup(byte[] plaintext, byte[] key)
    {
        ReadOnlySpan<byte> magic = "WXB1"u8;
        const int nonceLength = 12;
        const int authenticationTagLength = 16;
        var nonce = RandomNumberGenerator.GetBytes(nonceLength);
        var authenticationTag = new byte[authenticationTagLength];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(key, authenticationTagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, authenticationTag, magic);
        var encrypted = new byte[magic.Length + nonce.Length + authenticationTag.Length + ciphertext.Length];
        magic.CopyTo(encrypted);
        nonce.CopyTo(encrypted, magic.Length);
        authenticationTag.CopyTo(encrypted, magic.Length + nonce.Length);
        ciphertext.CopyTo(encrypted, magic.Length + nonce.Length + authenticationTag.Length);
        return encrypted;
    }

    /// <summary>表示测试只需要的备份标识。</summary>
    private sealed record BackupItem(Guid Id);

    /// <summary>表示群提及竞态测试只需要的群主键。</summary>
    private sealed record GroupItem(Guid Id);
}
