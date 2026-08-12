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

public sealed class SafetyGateIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task Paused_automation_persists_a_non_accepted_group_mention_decision()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client);
        await ActivateTargetAsync(client, ServiceTargetKind.Group, group.Id);

        var acceptedRequest = CreateMention(group.Id, $"accepted-{Guid.NewGuid():N}");
        var acceptedResponse = await client.PostAsJsonAsync("/api/group-mentions", acceptedRequest, JsonOptions);
        var accepted = await acceptedResponse.Content.ReadFromJsonAsync<MentionItem>(JsonOptions);
        Assert.Equal(MentionDecision.Accepted, accepted!.Decision);

        await SetAutomationPausedAsync(client, true);
        var pausedRequest = CreateMention(group.Id, $"paused-{Guid.NewGuid():N}");
        var pausedResponse = await client.PostAsJsonAsync("/api/group-mentions", pausedRequest, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, pausedResponse.StatusCode);
        var paused = await pausedResponse.Content.ReadFromJsonAsync<MentionItem>(JsonOptions);
        Assert.Equal(MentionDecision.AutomationPaused, paused!.Decision);
        Assert.NotEqual(MentionDecision.Accepted, paused.Decision);
        Assert.Null(paused.EntitlementId);
        Assert.False(paused.Duplicate);
        Assert.False(string.IsNullOrWhiteSpace(paused.SuggestedMessage));

        var audits = await client.GetFromJsonAsync<List<AuditItem>>(
            "/api/audit-logs?action=group-mention.automation-paused",
            JsonOptions);
        Assert.Contains(audits!, x => x.ResourceId == paused.Id.ToString("D") && !x.Success);

        await SetAutomationPausedAsync(client, false);
        var replayResponse = await client.PostAsJsonAsync("/api/group-mentions", pausedRequest, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<MentionItem>(JsonOptions);
        Assert.Equal(paused.Id, replay!.Id);
        Assert.Equal(MentionDecision.AutomationPaused, replay.Decision);
        Assert.True(replay.Duplicate);
    }

    [Fact]
    public async Task Paused_automation_rejects_new_remark_tasks_before_pending_and_audits()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var contact = await CreateContactAsync(client);
        var rule = await CreateRemarkRuleAsync(client);
        await SetAutomationPausedAsync(client, true);

        using var preview = await client.PostAsJsonAsync(
            "/api/remark-tasks/preview",
            new RemarkTaskRequest(rule.Id, contact.Id),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, preview.StatusCode);
        Assert.Contains("automation_paused", await preview.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var idempotencyKey = $"paused-remark-{Guid.NewGuid():N}";
        async Task<HttpResponseMessage> CreateTaskAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/remark-tasks")
            {
                Content = JsonContent.Create(new RemarkTaskRequest(rule.Id, contact.Id), options: JsonOptions)
            };
            request.Headers.Add("Idempotency-Key", idempotencyKey);
            return await client.SendAsync(request);
        }

        using var first = await CreateTaskAsync();
        using var second = await CreateTaskAsync();
        Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("automation_paused", await first.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("automation_paused", await second.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var tasks = await client.GetFromJsonAsync<List<RemarkTaskItem>>("/api/remark-tasks", JsonOptions);
        Assert.Empty(tasks!);
        var audits = await client.GetFromJsonAsync<List<AuditItem>>(
            "/api/audit-logs?action=remark-task.rejected.automation-paused",
            JsonOptions);
        Assert.All(audits!, x => Assert.False(x.Success));
        Assert.Contains(audits!, x => x.ResourceId == contact.Id.ToString("D"));
        var previewAudits = await client.GetFromJsonAsync<List<AuditItem>>(
            "/api/audit-logs?action=remark-task.preview-rejected.automation-paused",
            JsonOptions);
        Assert.Contains(previewAudits!, x => x.ResourceId == contact.Id.ToString("D") && !x.Success);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Remark_completion_rejects_changes_to_either_original_remark(bool changeSystemRemark)
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var contact = await CreateContactAsync(client);
        await ActivateTargetAsync(client, ServiceTargetKind.Contact, contact.Id);
        var rule = await CreateRemarkRuleAsync(client);
        var task = await CreateRemarkTaskAsync(client, rule.Id, contact.Id);
        Assert.Equal(RemarkTaskStatus.Pending, task.Status);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var target = await db.Contacts.IgnoreQueryFilters().SingleAsync(x => x.Id == contact.Id);
            if (changeSystemRemark)
            {
                target.SystemRemark = "concurrent system remark";
            }
            else
            {
                target.CurrentWeChatRemark = "manual WeChat remark";
            }
            target.Version++;
            await db.SaveChangesAsync();
        }

        var completion = await client.PostAsJsonAsync(
            $"/api/remark-tasks/{task.Id:D}/complete",
            new RemarkTaskCompleteRequest(task.Version, true, task.GeneratedRemark, null),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, completion.StatusCode);
        Assert.Contains("remark_target_changed", await completion.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var unchangedTask = await client.GetFromJsonAsync<RemarkTaskItem>(
            $"/api/remark-tasks/{task.Id:D}",
            JsonOptions);
        Assert.Equal(RemarkTaskStatus.Pending, unchangedTask!.Status);
        Assert.Equal(task.Version, unchangedTask.Version);
        Assert.Null(unchangedTask.CompletedAt);

        var unchangedTarget = await client.GetFromJsonAsync<ContactItem>($"/api/contacts/{contact.Id:D}", JsonOptions);
        if (changeSystemRemark)
        {
            Assert.Equal("concurrent system remark", unchangedTarget!.SystemRemark);
            Assert.Null(unchangedTarget.CurrentWeChatRemark);
        }
        else
        {
            Assert.Null(unchangedTarget!.SystemRemark);
            Assert.Equal("manual WeChat remark", unchangedTarget.CurrentWeChatRemark);
        }

        var audits = await client.GetFromJsonAsync<List<AuditItem>>(
            "/api/audit-logs?action=remark-task.rejected.target-changed",
            JsonOptions);
        Assert.Contains(audits!, x => x.ResourceId == task.Id.ToString("D") && !x.Success);
    }

    private static GroupMentionRequest CreateMention(Guid groupId, string externalEventId) =>
        new(externalEventId, groupId, "member-1", "@bot status", true, false, DateTimeOffset.UtcNow);

    private static async Task SetAutomationPausedAsync(HttpClient client, bool paused)
    {
        var state = await client.GetFromJsonAsync<SystemState>("/api/system-state", JsonOptions);
        var response = await client.PutAsJsonAsync(
            "/api/system-state/automation",
            new AutomationStateRequest(
                state!.Version,
                paused,
                paused ? "integration safety pause" : "integration safety resume"),
            JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
    }

    private static async Task ActivateTargetAsync(
        HttpClient client,
        ServiceTargetKind targetKind,
        Guid targetId)
    {
        var issueResponse = await client.PostAsJsonAsync(
            "/api/activation-codes",
            new IssueActivationCodeRequest("BASIC", ServiceDurationKind.Days30, null),
            JsonOptions);
        var issued = await issueResponse.Content.ReadFromJsonAsync<IssuedCode>(JsonOptions);
        using var redeem = new HttpRequestMessage(HttpMethod.Post, "/api/activation-codes/redeem")
        {
            Content = JsonContent.Create(
                new RedeemActivationCodeRequest(issued!.Code, targetKind, targetId),
                options: JsonOptions)
        };
        redeem.Headers.Add("Idempotency-Key", $"activate-{targetKind}-{targetId:N}");
        var response = await client.SendAsync(redeem);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
    }

    private static async Task<GroupItem> CreateGroupAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/groups",
            new GroupCreateRequest(
                $"safety-group-{Guid.NewGuid():N}",
                "Safety group",
                "Safety tests",
                null,
                false,
                null),
            JsonOptions);
        return (await response.Content.ReadFromJsonAsync<GroupItem>(JsonOptions))!;
    }

    private static async Task<ContactItem> CreateContactAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/contacts",
            new ContactCreateRequest(
                $"safety-contact-{Guid.NewGuid():N}",
                "Safety contact",
                "wx-safety",
                "C-SAFE",
                null,
                false,
                null),
            JsonOptions);
        return (await response.Content.ReadFromJsonAsync<ContactItem>(JsonOptions))!;
    }

    private static async Task<RuleItem> CreateRemarkRuleAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/remark-rules",
            new RemarkRuleCreateRequest(
                $"safety-rule-{Guid.NewGuid():N}",
                ServiceTargetKind.Contact,
                "{customerCode}-{displayName}",
                RemarkConflictPolicy.OverwriteSystemGeneratedOnly,
                true,
                64),
            JsonOptions);
        return (await response.Content.ReadFromJsonAsync<RuleItem>(JsonOptions))!;
    }

    private static async Task<RemarkTaskItem> CreateRemarkTaskAsync(
        HttpClient client,
        Guid ruleId,
        Guid contactId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/remark-tasks")
        {
            Content = JsonContent.Create(new RemarkTaskRequest(ruleId, contactId), options: JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", $"snapshot-{Guid.NewGuid():N}");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<RemarkTaskItem>(body, JsonOptions)!;
    }

    private sealed record IssuedCode(string Code);
    private sealed record GroupItem(Guid Id);
    private sealed record RuleItem(Guid Id);
    private sealed record SystemState(bool AutomationPaused, long Version);
    private sealed record MentionItem(
        Guid Id,
        MentionDecision Decision,
        Guid? EntitlementId,
        string? SuggestedMessage,
        bool Duplicate);
    private sealed record AuditItem(string ResourceId, bool Success);
    private sealed record RemarkTaskItem(
        Guid Id,
        string GeneratedRemark,
        RemarkTaskStatus Status,
        DateTimeOffset? CompletedAt,
        long Version);
    private sealed record ContactItem(
        Guid Id,
        string? SystemRemark,
        string? CurrentWeChatRemark);
}
