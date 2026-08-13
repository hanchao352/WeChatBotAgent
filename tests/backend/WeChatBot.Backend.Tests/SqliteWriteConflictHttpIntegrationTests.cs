using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Domain;

namespace WeChatBot.Backend.Tests;

public sealed class SqliteWriteConflictHttpIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task Renew_returns_database_write_busy_when_sqlite_lock_persists()
    {
        using var factory = CreateFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        await CreateReadyTaskAsync(admin);
        var identity = await RegisterHealthyAgentAsync(agent);
        var lease = await ClaimAsync(agent, identity);

        await using var blockerConnection = await OpenLockConnectionAsync(factory);
        await using var blockerTransaction = blockerConnection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: true);
        await AcquireLockAsync(
            blockerConnection,
            blockerTransaction,
            "UPDATE RemarkTasks SET Version = Version WHERE 0 = 1;");

        using var response = await agent.PostAsJsonAsync(
            $"/api/agents/{identity.AgentId}/remark-tasks/{lease.TaskId:D}/renew",
            new RemarkTaskLeaseRequest(identity.InstanceId, lease.LeaseToken, lease.Version),
            JsonOptions);

        await AssertDatabaseWriteBusyAsync(response);
        await blockerTransaction.RollbackAsync();
    }

    [Fact]
    public async Task Agent_group_mention_returns_database_write_busy_when_sqlite_lock_persists()
    {
        using var factory = CreateFactory();
        using var admin = factory.CreateAuthenticatedClient();
        using var agent = factory.CreateAgentClient();
        var group = await CreateGroupAsync(admin);
        var identity = await RegisterHealthyAgentAsync(agent);

        await using var blockerConnection = await OpenLockConnectionAsync(factory);
        await using var blockerTransaction = blockerConnection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: true);
        await AcquireLockAsync(
            blockerConnection,
            blockerTransaction,
            "UPDATE GroupMentions SET CapturedAt = CapturedAt WHERE 0 = 1;");

        using var response = await agent.PostAsJsonAsync(
            $"/api/agents/{identity.AgentId}/group-mentions",
            new AgentGroupMentionRequest(
                identity.InstanceId,
                new GroupMentionRequest(
                    $"busy-event-{Guid.NewGuid():N}",
                    group.Id,
                    "busy-sender",
                    "Persistent SQLite write-lock regression test.",
                    false,
                    false,
                    DateTimeOffset.UtcNow)),
            JsonOptions);

        await AssertDatabaseWriteBusyAsync(response);
        await blockerTransaction.RollbackAsync();
    }

    private static TestApplicationFactory CreateFactory() =>
        new(new Dictionary<string, string?>(), databaseDefaultTimeoutSeconds: 1);

    private static async Task<AgentIdentity> RegisterHealthyAgentAsync(HttpClient agent)
    {
        var identity = new AgentIdentity(
            $"busy-agent-{Guid.NewGuid():N}",
            $"wx-busy-{Guid.NewGuid():N}");
        var sentAt = DateTimeOffset.UtcNow;
        using var response = await agent.PostAsJsonAsync(
            "/api/agents/heartbeat",
            new AgentHeartbeatRequest(
                identity.AgentId,
                identity.InstanceId,
                sentAt,
                new AgentRuntimeSnapshotRequest(
                    AgentOperatingState.Healthy,
                    "HEALTHY",
                    "SQLite write-conflict HTTP integration test.",
                    sentAt,
                    null,
                    null),
                0,
                0,
                true,
                "1.0.0-test"),
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return identity;
    }

    private static async Task CreateReadyTaskAsync(HttpClient admin)
    {
        using var contactResponse = await admin.PostAsJsonAsync(
            "/api/contacts",
            new ContactCreateRequest(
                $"busy-contact-{Guid.NewGuid():N}",
                "SQLite busy contact",
                $"wx-{Guid.NewGuid():N}",
                $"C-{Guid.NewGuid():N}"[..18],
                null,
                false,
                null),
            JsonOptions);
        var contact = await ReadSuccessfulAsync<Identifier>(contactResponse);
        using var codeResponse = await admin.PostAsJsonAsync(
            "/api/activation-codes",
            new IssueActivationCodeRequest("BASIC", ServiceDurationKind.Days30, null),
            JsonOptions);
        var code = await ReadSuccessfulAsync<IssuedCode>(codeResponse);
        using var redeemRequest = new HttpRequestMessage(HttpMethod.Post, "/api/activation-codes/redeem")
        {
            Content = JsonContent.Create(
                new RedeemActivationCodeRequest(code.Code, ServiceTargetKind.Contact, contact.Id),
                options: JsonOptions)
        };
        redeemRequest.Headers.Add("Idempotency-Key", $"busy-entitlement-{Guid.NewGuid():N}");
        using var redeemResponse = await admin.SendAsync(redeemRequest);
        Assert.True(redeemResponse.IsSuccessStatusCode, await redeemResponse.Content.ReadAsStringAsync());
        using var ruleResponse = await admin.PostAsJsonAsync(
            "/api/remark-rules",
            new RemarkRuleCreateRequest(
                $"busy-rule-{Guid.NewGuid():N}",
                ServiceTargetKind.Contact,
                "{customerCode}-{displayName}",
                RemarkConflictPolicy.OverwriteSystemGeneratedOnly,
                true,
                64),
            JsonOptions);
        var rule = await ReadSuccessfulAsync<Identifier>(ruleResponse);
        using var taskRequest = new HttpRequestMessage(HttpMethod.Post, "/api/remark-tasks")
        {
            Content = JsonContent.Create(new RemarkTaskRequest(rule.Id, contact.Id), options: JsonOptions)
        };
        taskRequest.Headers.Add("Idempotency-Key", $"busy-task-{Guid.NewGuid():N}");
        using var taskResponse = await admin.SendAsync(taskRequest);
        _ = await ReadSuccessfulAsync<Identifier>(taskResponse);
    }

    private static async Task<Identifier> CreateGroupAsync(HttpClient admin)
    {
        using var response = await admin.PostAsJsonAsync(
            "/api/groups",
            new GroupCreateRequest(
                $"busy-group-{Guid.NewGuid():N}",
                "SQLite busy group",
                null,
                null,
                false,
                null),
            JsonOptions);
        return await ReadSuccessfulAsync<Identifier>(response);
    }

    private static async Task<RemarkTaskLeaseResponse> ClaimAsync(
        HttpClient agent,
        AgentIdentity identity)
    {
        using var response = await agent.PostAsJsonAsync(
            $"/api/agents/{identity.AgentId}/remark-tasks/claim",
            new RemarkTaskClaimRequest(identity.InstanceId),
            JsonOptions);
        return await ReadSuccessfulAsync<RemarkTaskLeaseResponse>(response);
    }

    private static async Task<SqliteConnection> OpenLockConnectionAsync(
        TestApplicationFactory factory)
    {
        var connection = new SqliteConnection(
            $"Data Source={factory.DatabasePath};Default Timeout=1;Pooling=False");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task AcquireLockAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ReadSuccessfulAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<T>(body, JsonOptions)!;
    }

    private static async Task AssertDatabaseWriteBusyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(
            "database_write_busy",
            problem.RootElement.GetProperty("errorCode").GetString());
    }

    private sealed record AgentIdentity(string AgentId, string InstanceId);

    private sealed record Identifier(Guid Id);

    private sealed record IssuedCode(string Code);
}
