using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;

namespace WeChatBot.Backend.Tests;

public sealed class AgentApiIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task Heartbeat_enforces_agent_scoped_api_key()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat("unauthenticated-agent", "wx-unauthenticated", DateTimeOffset.UtcNow),
            JsonOptions);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var admin = factory.CreateAuthenticatedClient();
        var adminHeartbeat = await admin.PostAsJsonAsync(
            "/api/agents/heartbeat",
            CreateHeartbeat("admin-agent", "wx-admin", DateTimeOffset.UtcNow),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, adminHeartbeat.StatusCode);

        using var agent = factory.CreateAgentClient();
        var agentListing = await agent.GetAsync("/api/agents");
        Assert.Equal(HttpStatusCode.Forbidden, agentListing.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_registers_updates_and_rejects_another_wechat_binding()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAgentClient();
        using var admin = factory.CreateAuthenticatedClient();
        var agentId = $"agent-{Guid.NewGuid():N}";
        var firstSentAt = DateTimeOffset.UtcNow.AddSeconds(-2);
        var first = await PostHeartbeatAsync(client, CreateHeartbeat(
            agentId,
            "wx-primary",
            firstSentAt,
            queueDepth: 2));

        Assert.True(first.Accepted);
        Assert.False(first.EmergencyStop);
        Assert.Equal("1", first.ConfigurationVersion);

        var latestSentAt = firstSentAt.AddSeconds(2);
        var latest = CreateHeartbeat(agentId, "wx-primary", latestSentAt, queueDepth: 19);
        Assert.True((await PostHeartbeatAsync(client, latest)).Accepted);

        var stale = CreateHeartbeat(agentId, "wx-primary", firstSentAt.AddSeconds(1), queueDepth: 7);
        Assert.True((await PostHeartbeatAsync(client, stale)).Accepted);

        var rejected = await PostHeartbeatAsync(
            client,
            CreateHeartbeat(agentId, "wx-other", latestSentAt.AddSeconds(1), queueDepth: 99));
        Assert.False(rejected.Accepted);
        var repeatedRejection = await PostHeartbeatAsync(
            client,
            CreateHeartbeat(agentId, "wx-other", latestSentAt.AddSeconds(2), queueDepth: 100));
        Assert.False(repeatedRejection.Accepted);

        var agents = await admin.GetFromJsonAsync<List<AgentListItem>>("/api/agents", JsonOptions);
        var stored = Assert.Single(agents!, x => x.AgentId == agentId);
        Assert.Equal("wx-primary", stored.WeChatInstanceId);
        Assert.Equal(latestSentAt, stored.SentAt);
        Assert.Equal(19, stored.QueueDepth);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registrationAudits = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(x => x.ResourceId == stored.Id.ToString("D") && x.Action == "agent.registered");
        var heartbeatAudits = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(x => x.ResourceId == stored.Id.ToString("D") && x.Action.StartsWith("agent.heartbeat"));
        Assert.Equal(1, registrationAudits);
        Assert.Equal(1, heartbeatAudits);
    }

    [Fact]
    public async Task Concurrent_first_heartbeats_create_one_registration()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAgentClient();
        using var admin = factory.CreateAuthenticatedClient();
        var agentId = $"concurrent-{Guid.NewGuid():N}";
        var sentAt = DateTimeOffset.UtcNow;
        var requests = Enumerable.Range(0, 25)
            .Select(index => PostHeartbeatResponseAsync(
                client,
                CreateHeartbeat(agentId, "wx-concurrent", sentAt.AddTicks(index), queueDepth: index)))
            .ToArray();

        var responses = await Task.WhenAll(requests);
        var failures = responses.Where(x => !x.IsSuccessStatusCode)
            .Select(x => $"{(int)x.StatusCode}: {x.Content.ReadAsStringAsync().GetAwaiter().GetResult()}")
            .ToArray();
        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));

        var agents = await admin.GetFromJsonAsync<List<AgentListItem>>("/api/agents", JsonOptions);
        var stored = Assert.Single(agents!, x => x.AgentId == agentId);
        Assert.Equal(24, stored.QueueDepth);
    }

    [Fact]
    public async Task Automation_pause_is_returned_as_emergency_stop()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAgentClient();
        using var admin = factory.CreateAuthenticatedClient();
        var state = await admin.GetFromJsonAsync<SystemState>("/api/system-state", JsonOptions);
        if (!state!.AutomationPaused)
        {
            var pause = await admin.PutAsJsonAsync(
                "/api/system-state/automation",
                new AutomationStateRequest(state.Version, true, "integration emergency stop"),
                JsonOptions);
            Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        }

        var result = await PostHeartbeatAsync(
            client,
            CreateHeartbeat($"paused-{Guid.NewGuid():N}", "wx-paused", DateTimeOffset.UtcNow));
        Assert.True(result.Accepted);
        Assert.True(result.EmergencyStop);
    }

    [Fact]
    public async Task Restore_preserves_newer_telemetry_and_never_restores_stale_online_state()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAgentClient();
        using var admin = factory.CreateAuthenticatedClient();
        var preservedAgentId = $"preserved-{Guid.NewGuid():N}";
        var restoredAgentId = $"restored-{Guid.NewGuid():N}";
        var oldSentAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        await PostHeartbeatAsync(client, CreateHeartbeat(preservedAgentId, "wx-preserved", oldSentAt, queueDepth: 1));
        await PostHeartbeatAsync(client, CreateHeartbeat(restoredAgentId, "wx-restored", oldSentAt, queueDepth: 2));

        var backupResponse = await admin.PostAsJsonAsync(
            "/api/backups",
            new CreateBackupRequest("agent backup scope"),
            JsonOptions);
        var backupBody = await backupResponse.Content.ReadAsStringAsync();
        Assert.True(backupResponse.IsSuccessStatusCode, backupBody);
        var backup = JsonSerializer.Deserialize<BackupItem>(backupBody, JsonOptions)!;

        var newSentAt = oldSentAt.AddSeconds(4);
        await PostHeartbeatAsync(client, CreateHeartbeat(preservedAgentId, "wx-preserved", newSentAt, queueDepth: 77));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var registration = await db.AgentRegistrations.IgnoreQueryFilters()
                .SingleAsync(x => x.AgentId == restoredAgentId);
            db.AgentRegistrations.Remove(registration);
            await db.SaveChangesAsync();
        }

        using var restore = new HttpRequestMessage(HttpMethod.Post, $"/api/backups/{backup.Id:D}/restore")
        {
            Content = JsonContent.Create(new RestoreBackupRequest("RESTORE"), options: JsonOptions)
        };
        restore.Headers.Add("Idempotency-Key", $"agent-restore-{backup.Id:N}");
        var restoreResponse = await admin.SendAsync(restore);
        Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);

        var agents = await admin.GetFromJsonAsync<List<AgentListItem>>("/api/agents", JsonOptions);
        var preserved = Assert.Single(agents!, x => x.AgentId == preservedAgentId);
        Assert.Equal(newSentAt, preserved.SentAt);
        Assert.Equal(77, preserved.QueueDepth);

        var restored = Assert.Single(agents!, x => x.AgentId == restoredAgentId);
        Assert.Null(restored.ReceivedAt);
        Assert.Null(restored.QueueDepth);
        Assert.False(restored.Online);
    }

    private static AgentHeartbeatRequest CreateHeartbeat(
        string agentId,
        string weChatInstanceId,
        DateTimeOffset sentAt,
        int queueDepth = 0) =>
        new(
            agentId,
            weChatInstanceId,
            sentAt,
            new AgentRuntimeSnapshotRequest(
                AgentOperatingState.Healthy,
                "HEALTHY",
                "Agent self-check completed.",
                sentAt,
                null,
                null),
            queueDepth,
            0,
            false,
            "1.0.0-test");

    private static async Task<AgentHeartbeatResponse> PostHeartbeatAsync(
        HttpClient client,
        AgentHeartbeatRequest heartbeat)
    {
        using var response = await PostHeartbeatResponseAsync(client, heartbeat);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<AgentHeartbeatResponse>(body, JsonOptions)!;
    }

    private static Task<HttpResponseMessage> PostHeartbeatResponseAsync(
        HttpClient client,
        AgentHeartbeatRequest heartbeat) =>
        client.PostAsJsonAsync("/api/agents/heartbeat", heartbeat, JsonOptions);

    private sealed record BackupItem(Guid Id);
    private sealed record SystemState(bool AutomationPaused, long Version);
}
