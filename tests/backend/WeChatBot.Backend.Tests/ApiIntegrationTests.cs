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

public sealed class ApiIntegrationTests : IClassFixture<TestApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly TestApplicationFactory _factory;

    public ApiIntegrationTests(TestApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Protected_endpoint_rejects_missing_api_key_with_problem_details()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/contacts");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("unauthorized", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("traceId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Activation_is_concurrent_idempotent_and_enables_deduplicated_group_mentions()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client, "activation-test-group");
        var issueResponse = await client.PostAsJsonAsync("/api/activation-codes", new IssueActivationCodeRequest(
            "BASIC", ServiceDurationKind.Days30, null), JsonOptions);
        Assert.Equal(HttpStatusCode.Created, issueResponse.StatusCode);
        var issued = await issueResponse.Content.ReadFromJsonAsync<IssuedCode>(JsonOptions);
        Assert.NotNull(issued);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.ActivationCodes.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == issued.Id);
            Assert.NotEqual(issued.Code, stored.CodeHash);
            Assert.Equal(64, stored.CodeHash.Length);
        }

        var redemptions = Enumerable.Range(0, 100).Select(async index =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/activation-codes/redeem")
            {
                Content = JsonContent.Create(new RedeemActivationCodeRequest(issued.Code, ServiceTargetKind.Group, group.Id), options: JsonOptions)
            };
            request.Headers.Add("Idempotency-Key", $"redeem-{group.Id:N}-{index}");
            return await client.SendAsync(request);
        });
        var responses = await Task.WhenAll(redemptions);
        var failures = responses.Where(x => x.StatusCode != HttpStatusCode.OK)
            .Select(x => $"{(int)x.StatusCode}: {x.Content.ReadAsStringAsync().GetAwaiter().GetResult()}")
            .ToArray();
        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));
        var results = await Task.WhenAll(responses.Select(x => x.Content.ReadFromJsonAsync<Redemption>(JsonOptions)));
        Assert.Single(results.Select(x => x!.EntitlementId).Distinct());

        var entitlements = await client.GetFromJsonAsync<List<EntitlementItem>>(
            $"/api/entitlements?targetKind=group&targetId={group.Id:D}", JsonOptions);
        Assert.NotNull(entitlements);
        Assert.Single(entitlements);
        Assert.Equal("active", entitlements[0].EffectiveStatus);

        var capturedAt = DateTimeOffset.UtcNow;
        var mention = new GroupMentionRequest(
            $"mention-{Guid.NewGuid():N}", group.Id, "member-1", "@机器人 查询服务", true, false, capturedAt);
        var first = await client.PostAsJsonAsync("/api/group-mentions", mention, JsonOptions);
        var second = await client.PostAsJsonAsync("/api/group-mentions", mention, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<MentionResult>(JsonOptions);
        var secondBody = await second.Content.ReadFromJsonAsync<MentionResult>(JsonOptions);
        Assert.Equal(MentionDecision.Accepted, firstBody!.Decision);
        Assert.False(firstBody.Duplicate);
        Assert.True(secondBody!.Duplicate);
        Assert.Equal(firstBody.Id, secondBody.Id);
    }

    [Fact]
    public async Task Inactive_group_gets_activation_message_and_bot_messages_never_loop()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client, "inactive-group");
        var inactive = new GroupMentionRequest(
            $"mention-{Guid.NewGuid():N}", group.Id, "member-2", "@机器人 hello", true, false, DateTimeOffset.UtcNow);
        var inactiveResponse = await client.PostAsJsonAsync("/api/group-mentions", inactive, JsonOptions);
        var inactiveResult = await inactiveResponse.Content.ReadFromJsonAsync<MentionResult>(JsonOptions);
        Assert.Equal(MentionDecision.ActivationRequired, inactiveResult!.Decision);
        Assert.Contains("激活", inactiveResult.SuggestedMessage);

        var self = inactive with { ExternalEventId = $"mention-{Guid.NewGuid():N}", SenderIsBot = true };
        var selfResponse = await client.PostAsJsonAsync("/api/group-mentions", self, JsonOptions);
        var selfResult = await selfResponse.Content.ReadFromJsonAsync<MentionResult>(JsonOptions);
        Assert.Equal(MentionDecision.IgnoredBotMessage, selfResult!.Decision);
    }

    [Fact]
    public async Task Logical_restore_recovers_configuration_and_keeps_automation_paused()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var contact = await CreateContactAsync(client, $"backup-{Guid.NewGuid():N}", "Before backup");
        var backupResponse = await CreateBackupResponseAsync(client, "integration test");
        Assert.Equal(HttpStatusCode.Created, backupResponse.StatusCode);
        var manifest = await backupResponse.Content.ReadFromJsonAsync<BackupItem>(JsonOptions);
        Assert.NotNull(manifest);

        var update = new ContactUpdateRequest(
            contact.Version,
            contact.ExternalId,
            "After backup",
            contact.WeChatId,
            contact.CustomerCode,
            contact.CurrentWeChatRemark,
            contact.ManualRemarkProtected,
            contact.ServiceExpiresAt);
        var updateResponse = await client.PutAsJsonAsync($"/api/contacts/{contact.Id:D}", update, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var restoreKey = $"restore-{manifest.Id:N}";
        using var restoreRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/backups/{manifest.Id:D}/restore")
        {
            Content = JsonContent.Create(new RestoreBackupRequest("RESTORE"), options: JsonOptions)
        };
        restoreRequest.Headers.Add("Idempotency-Key", restoreKey);
        var restoreResponse = await client.SendAsync(restoreRequest);
        var restoreBody = await restoreResponse.Content.ReadAsStringAsync();
        Assert.True(restoreResponse.IsSuccessStatusCode, restoreBody);
        var firstRestore = JsonSerializer.Deserialize<RestoreItem>(restoreBody, JsonOptions);

        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/backups/{manifest.Id:D}/restore")
        {
            Content = JsonContent.Create(new RestoreBackupRequest("RESTORE"), options: JsonOptions)
        };
        replayRequest.Headers.Add("Idempotency-Key", restoreKey);
        var replayResponse = await client.SendAsync(replayRequest);
        var replayRestore = await replayResponse.Content.ReadFromJsonAsync<RestoreItem>(JsonOptions);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal(firstRestore!.RestoreId, replayRestore!.RestoreId);
        Assert.Equal("in-place-merge", firstRestore.Mode);
        Assert.False(firstRestore.IsolatedEnvironmentCreated);
        Assert.True(replayRestore.Replayed);

        var restored = await client.GetFromJsonAsync<ContactItem>($"/api/contacts/{contact.Id:D}", JsonOptions);
        Assert.Equal("Before backup", restored!.DisplayName);
        var state = await client.GetFromJsonAsync<SystemState>("/api/system-state", JsonOptions);
        Assert.True(state!.AutomationPaused);

        var verify = await client.PostAsync($"/api/backups/{manifest.Id:D}/verify", null);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.Contains("\"isValid\":true", await verify.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remark_task_is_idempotent_and_only_confirmation_updates_the_internal_remark()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var contact = await CreateContactAsync(client, $"remark-{Guid.NewGuid():N}", "Remark target");
        await ActivateTargetAsync(client, ServiceTargetKind.Contact, contact.Id);
        var ruleResponse = await client.PostAsJsonAsync("/api/remark-rules", new RemarkRuleCreateRequest(
            $"rule-{Guid.NewGuid():N}",
            ServiceTargetKind.Contact,
            "{customerCode}-{displayName}",
            RemarkConflictPolicy.OverwriteSystemGeneratedOnly,
            true,
            64), JsonOptions);
        Assert.Equal(HttpStatusCode.Created, ruleResponse.StatusCode);
        var rule = await ruleResponse.Content.ReadFromJsonAsync<RuleItem>(JsonOptions);

        var idempotencyKey = $"remark-task-{Guid.NewGuid():N}";
        async Task<HttpResponseMessage> CreateTaskAsync()
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/remark-tasks")
            {
                Content = JsonContent.Create(new RemarkTaskRequest(rule!.Id, contact.Id), options: JsonOptions)
            };
            message.Headers.Add("Idempotency-Key", idempotencyKey);
            return await client.SendAsync(message);
        }

        var first = await CreateTaskAsync();
        var second = await CreateTaskAsync();
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var task = await first.Content.ReadFromJsonAsync<RemarkTaskItem>(JsonOptions);
        var replay = await second.Content.ReadFromJsonAsync<RemarkTaskItem>(JsonOptions);
        Assert.Equal(task!.Id, replay!.Id);
        Assert.Equal(RemarkTaskStatus.Pending, task.Status);

        var beforeConfirmation = await client.GetFromJsonAsync<ContactItem>($"/api/contacts/{contact.Id:D}", JsonOptions);
        Assert.Null(beforeConfirmation!.SystemRemark);
        var complete = await client.PostAsJsonAsync($"/api/remark-tasks/{task.Id:D}/complete", new RemarkTaskCompleteRequest(
            task.Version, true, task.GeneratedRemark, null), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var afterConfirmation = await client.GetFromJsonAsync<ContactItem>($"/api/contacts/{contact.Id:D}", JsonOptions);
        Assert.Equal(task.GeneratedRemark, afterConfirmation!.SystemRemark);
        Assert.Equal(task.GeneratedRemark, afterConfirmation.CurrentWeChatRemark);
    }

    [Fact]
    public async Task Swagger_exposes_api_key_scheme_and_validation_returns_problem_details()
    {
        using var anonymous = _factory.CreateClient();
        var swagger = await anonymous.GetStringAsync("/swagger/v1/swagger.json");
        Assert.Contains("X-Api-Key", swagger, StringComparison.Ordinal);

        using var client = _factory.CreateAuthenticatedClient();
        var invalid = await client.PostAsJsonAsync("/api/groups", new GroupCreateRequest(
            string.Empty, string.Empty, null, null, false, null), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var body = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("validation_failed", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Activation_code_listing_is_masked_and_revocation_wins_before_redemption()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client, $"revocation-{Guid.NewGuid():N}");
        var issueResponse = await client.PostAsJsonAsync("/api/activation-codes", new IssueActivationCodeRequest(
            "ADVANCED_GENERAL", ServiceDurationKind.OneYear, null), JsonOptions);
        var issued = await issueResponse.Content.ReadFromJsonAsync<IssuedCode>(JsonOptions);
        Assert.NotNull(issued);

        var listResponse = await client.GetAsync("/api/activation-codes");
        var listJson = await listResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(issued.Code, listJson, StringComparison.Ordinal);
        Assert.DoesNotContain("codeHash", listJson, StringComparison.OrdinalIgnoreCase);
        var summaries = JsonSerializer.Deserialize<List<ActivationSummary>>(listJson, JsonOptions);
        var summary = Assert.Single(summaries!, x => x.Id == issued.Id);
        Assert.Equal("available", summary.Status);

        var revoke = await client.PostAsJsonAsync($"/api/activation-codes/{issued.Id:D}/revoke", new RevokeActivationCodeRequest(
            summary.Version, "issued for the wrong customer"), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal("revoked", (await revoke.Content.ReadFromJsonAsync<ActivationSummary>(JsonOptions))!.Status);

        using var redeem = new HttpRequestMessage(HttpMethod.Post, "/api/activation-codes/redeem")
        {
            Content = JsonContent.Create(new RedeemActivationCodeRequest(issued.Code, ServiceTargetKind.Group, group.Id), options: JsonOptions)
        };
        redeem.Headers.Add("Idempotency-Key", $"revoked-{issued.Id:N}");
        var redeemResponse = await client.SendAsync(redeem);
        Assert.Equal(HttpStatusCode.Conflict, redeemResponse.StatusCode);
        Assert.Contains("activation_code_revoked", await redeemResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restoring_an_older_backup_does_not_reactivate_a_redeemed_code()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client, $"anti-reactivation-{Guid.NewGuid():N}");
        var issueResponse = await client.PostAsJsonAsync("/api/activation-codes", new IssueActivationCodeRequest(
            "BASIC", ServiceDurationKind.Days60, null), JsonOptions);
        var issued = await issueResponse.Content.ReadFromJsonAsync<IssuedCode>(JsonOptions);
        var backupResponse = await CreateBackupResponseAsync(client, "before redemption");
        var backup = await backupResponse.Content.ReadFromJsonAsync<BackupItem>(JsonOptions);

        async Task<HttpResponseMessage> RedeemAsync(string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/activation-codes/redeem")
            {
                Content = JsonContent.Create(new RedeemActivationCodeRequest(issued!.Code, ServiceTargetKind.Group, group.Id), options: JsonOptions)
            };
            request.Headers.Add("Idempotency-Key", key);
            return await client.SendAsync(request);
        }

        var firstRedemptionResponse = await RedeemAsync($"first-{issued!.Id:N}");
        var firstRedemption = await firstRedemptionResponse.Content.ReadFromJsonAsync<Redemption>(JsonOptions);
        using var restore = new HttpRequestMessage(HttpMethod.Post, $"/api/backups/{backup!.Id:D}/restore")
        {
            Content = JsonContent.Create(new RestoreBackupRequest("RESTORE"), options: JsonOptions)
        };
        restore.Headers.Add("Idempotency-Key", $"anti-reactivation-{backup.Id:N}");
        var restoreResponse = await client.SendAsync(restore);
        Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);

        var replayResponse = await RedeemAsync($"after-restore-{issued.Id:N}");
        var replay = await replayResponse.Content.ReadFromJsonAsync<Redemption>(JsonOptions);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal(firstRedemption!.EntitlementId, replay!.EntitlementId);
        var entitlements = await client.GetFromJsonAsync<List<EntitlementItem>>(
            $"/api/entitlements?targetKind=group&targetId={group.Id:D}", JsonOptions);
        Assert.Single(entitlements!);
        var ledger = await client.GetFromJsonAsync<List<LedgerItem>>(
            $"/api/entitlements/{firstRedemption.EntitlementId:D}/ledger", JsonOptions);
        Assert.Single(ledger!, x => x.EventType == "activated");
    }

    private static async Task<GroupItem> CreateGroupAsync(HttpClient client, string externalId)
    {
        var response = await client.PostAsJsonAsync("/api/groups", new GroupCreateRequest(
            externalId, "Test group", "General service", null, false, null), JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<GroupItem>(body, JsonOptions)!;
    }

    private static async Task<ContactItem> CreateContactAsync(HttpClient client, string externalId, string displayName)
    {
        var response = await client.PostAsJsonAsync("/api/contacts", new ContactCreateRequest(
            externalId, displayName, "wx-test", "C-TEST", null, false, null), JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<ContactItem>(body, JsonOptions)!;
    }

    private static Task<HttpResponseMessage> CreateBackupResponseAsync(HttpClient client, string reason)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/backups")
        {
            Content = JsonContent.Create(new CreateBackupRequest(reason), options: JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", $"backup-{Guid.NewGuid():N}");
        return client.SendAsync(request);
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

    private sealed record IssuedCode(Guid Id, string Code);
    private sealed record ActivationSummary(Guid Id, string Status, long Version);
    private sealed record Redemption(Guid EntitlementId);
    private sealed record EntitlementItem(Guid Id, string EffectiveStatus);
    private sealed record LedgerItem(string EventType);
    private sealed record MentionResult(Guid Id, MentionDecision Decision, string? SuggestedMessage, bool Duplicate);
    private sealed record BackupItem(Guid Id);
    private sealed record RestoreItem(Guid RestoreId, string Mode, bool IsolatedEnvironmentCreated, bool Replayed);
    private sealed record SystemState(bool AutomationPaused);
    private sealed record GroupItem(Guid Id, string ExternalId, long Version);
    private sealed record RuleItem(Guid Id);
    private sealed record RemarkTaskItem(Guid Id, string GeneratedRemark, RemarkTaskStatus Status, long Version);
    private sealed record ContactItem(
        Guid Id,
        string ExternalId,
        string DisplayName,
        string? WeChatId,
        string? CustomerCode,
        string? SystemRemark,
        string? CurrentWeChatRemark,
        bool ManualRemarkProtected,
        DateTimeOffset? ServiceExpiresAt,
        long Version);
}
