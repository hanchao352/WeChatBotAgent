using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Services;

namespace WeChatBot.Backend.Tests;

public sealed class EntitlementFeatureIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void Feature_parser_is_case_insensitive_and_fails_closed_for_invalid_json()
    {
        Assert.True(PackageFeatureSet.Contains("[\"AUTO-REMARK\",\"other\"]", "auto-remark"));
        Assert.True(PackageFeatureSet.Contains("[\"group-mention\"]", "GROUP-MENTION"));
        Assert.False(PackageFeatureSet.Contains("{\"auto-remark\":true}", "auto-remark"));
        Assert.False(PackageFeatureSet.Contains("not-json", "auto-remark"));
        Assert.False(PackageFeatureSet.Contains("[42,null]", "auto-remark"));
    }

    [Fact]
    public async Task Basic_renews_in_sequence_while_advanced_general_uses_an_independent_track()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client);

        var firstBasic = await IssueAndRedeemAsync(
            client,
            "BASIC",
            ServiceDurationKind.Days30,
            ServiceTargetKind.Group,
            group.Id,
            "first-basic");
        var advanced = await IssueAndRedeemAsync(
            client,
            "ADVANCED_GENERAL",
            ServiceDurationKind.Days30,
            ServiceTargetKind.Group,
            group.Id,
            "advanced-independent");
        var secondBasic = await IssueAndRedeemAsync(
            client,
            "BASIC",
            ServiceDurationKind.Days30,
            ServiceTargetKind.Group,
            group.Id,
            "second-basic");

        Assert.Equal("active", firstBasic.Status);
        Assert.Equal("active", advanced.Status);
        Assert.Equal("scheduled", secondBasic.Status);
        Assert.Equal(firstBasic.EndsAt!.Value, secondBasic.StartsAt);
        Assert.Equal(secondBasic.StartsAt.AddDays(30), secondBasic.EndsAt);
        Assert.True(advanced.StartsAt < firstBasic.EndsAt.Value);

        var entitlements = await GetEntitlementsAsync(client, ServiceTargetKind.Group, group.Id);
        Assert.Equal(3, entitlements.Count);
        Assert.Equal(2, entitlements.Count(x => x.PackageCode == "BASIC"));
        Assert.Single(entitlements, x => x.PackageCode == "ADVANCED_GENERAL");
        var ledger = await client.GetFromJsonAsync<List<LedgerItem>>(
            $"/api/entitlements/{secondBasic.EntitlementId:D}/ledger",
            JsonOptions);
        Assert.Single(ledger!, x => x.EventType == "scheduled");
    }

    [Fact]
    public async Task Permanent_is_scheduled_after_finite_time_and_blocks_later_finite_redemption_without_consuming_it()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client);

        var finite = await IssueAndRedeemAsync(
            client,
            "BASIC",
            ServiceDurationKind.Days60,
            ServiceTargetKind.Group,
            group.Id,
            "finite-before-permanent");
        var permanent = await IssueAndRedeemAsync(
            client,
            "BASIC",
            ServiceDurationKind.Permanent,
            ServiceTargetKind.Group,
            group.Id,
            "permanent-upgrade");
        Assert.Equal(finite.EndsAt!.Value, permanent.StartsAt);
        Assert.Null(permanent.EndsAt);
        Assert.Equal("scheduled", permanent.Status);

        var rejectedCode = await IssueCodeAsync(client, "BASIC", ServiceDurationKind.Days30);
        using var rejected = await RedeemResponseAsync(
            client,
            rejectedCode.Code,
            ServiceTargetKind.Group,
            group.Id,
            "finite-after-permanent");
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains(
            "permanent_entitlement_exists",
            await rejected.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        var codeList = await client.GetFromJsonAsync<List<ActivationSummary>>("/api/activation-codes", JsonOptions);
        var unchangedCode = Assert.Single(codeList!, x => x.Id == rejectedCode.Id);
        Assert.Equal("available", unchangedCode.Status);
        var entitlements = await GetEntitlementsAsync(client, ServiceTargetKind.Group, group.Id);
        Assert.Equal(2, entitlements.Count(x => x.PackageCode == "BASIC"));
    }

    [Fact]
    public async Task Disabled_package_rejects_redemption_without_consuming_the_code_or_writing_entitlement_state()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        const string packageCode = "DISABLED_AFTER_ISSUE";
        await AddPackageAsync(factory, packageCode, "[\"group-mention\"]", PackageTier.Basic);
        var group = await CreateGroupAsync(client);
        var code = await IssueCodeAsync(client, packageCode, ServiceDurationKind.Days30);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var package = await db.ServicePackages.SingleAsync(x => x.Code == packageCode);
            package.IsEnabled = false;
            await db.SaveChangesAsync();
        }

        using var response = await RedeemResponseAsync(
            client,
            code.Code,
            ServiceTargetKind.Group,
            group.Id,
            "disabled-package");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            "service_package_disabled",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        var codeList = await client.GetFromJsonAsync<List<ActivationSummary>>("/api/activation-codes", JsonOptions);
        Assert.Equal("available", Assert.Single(codeList!, x => x.Id == code.Id).Status);
        Assert.Empty(await GetEntitlementsAsync(client, ServiceTargetKind.Group, group.Id));

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedCode = await verificationDb.ActivationCodes
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(x => x.Id == code.Id);
        Assert.Null(storedCode.RedeemedAt);
        Assert.Null(storedCode.EntitlementId);
        Assert.Empty(await verificationDb.Entitlements.IgnoreQueryFilters().AsNoTracking().ToListAsync());
        Assert.Empty(await verificationDb.EntitlementLedger.IgnoreQueryFilters().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Concurrent_distinct_codes_form_one_contiguous_package_track_and_replay_idempotently()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client);
        var codes = new List<IssuedCode>();
        for (var index = 0; index < 6; index++)
        {
            codes.Add(await IssueCodeAsync(client, "BASIC", ServiceDurationKind.Days30));
        }

        var requests = codes.Select((code, index) => RedeemResponseAsync(
            client,
            code.Code,
            ServiceTargetKind.Group,
            group.Id,
            $"concurrent-track-{index}"));
        var responses = await Task.WhenAll(requests);
        try
        {
            var failures = responses.Where(x => !x.IsSuccessStatusCode)
                .Select(x => $"{(int)x.StatusCode}: {x.Content.ReadAsStringAsync().GetAwaiter().GetResult()}")
                .ToArray();
            Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));
            var results = await Task.WhenAll(responses.Select(x => x.Content.ReadFromJsonAsync<Redemption>(JsonOptions)));
            Assert.Equal(6, results.Select(x => x!.EntitlementId).Distinct().Count());
            var ordered = results.Select(x => x!).OrderBy(x => x.StartsAt).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                Assert.Equal(ordered[index].StartsAt.AddDays(30), ordered[index].EndsAt);
                if (index > 0) Assert.Equal(ordered[index - 1].EndsAt!.Value, ordered[index].StartsAt);
            }

            using var replay = await RedeemResponseAsync(
                client,
                codes[0].Code,
                ServiceTargetKind.Group,
                group.Id,
                "concurrent-track-0");
            var replayResult = await replay.Content.ReadFromJsonAsync<Redemption>(JsonOptions);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.True(replayResult!.Replayed);
            Assert.Contains(replayResult.EntitlementId, ordered.Select(x => x.EntitlementId));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task Group_mention_requires_its_feature_and_accepts_case_insensitive_feature_names()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        await AddPackageAsync(factory, "AUTO_ONLY", "[\"AUTO-REMARK\"]", PackageTier.Basic);
        await AddPackageAsync(factory, "GROUP_UPPER", "[\"GROUP-MENTION\"]", PackageTier.Basic);
        var group = await CreateGroupAsync(client);

        await IssueAndRedeemAsync(
            client,
            "AUTO_ONLY",
            ServiceDurationKind.Days30,
            ServiceTargetKind.Group,
            group.Id,
            "wrong-group-feature");
        var missingFeature = await PostMentionAsync(client, group.Id, "missing-group-feature");
        Assert.Equal(MentionDecision.ActivationRequired, missingFeature.Decision);
        Assert.Contains("group-mention", missingFeature.DecisionReason, StringComparison.OrdinalIgnoreCase);

        await IssueAndRedeemAsync(
            client,
            "GROUP_UPPER",
            ServiceDurationKind.Days30,
            ServiceTargetKind.Group,
            group.Id,
            "correct-group-feature");
        var accepted = await PostMentionAsync(client, group.Id, "case-insensitive-group-feature");
        Assert.Equal(MentionDecision.Accepted, accepted.Decision);
        Assert.NotNull(accepted.EntitlementId);
    }

    [Fact]
    public async Task Auto_remark_requires_its_feature_on_creation_and_completion()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        await AddPackageAsync(factory, "GROUP_ONLY", "[\"group-mention\"]", PackageTier.Basic);
        await AddPackageAsync(factory, "AUTO_UPPER", "[\"AUTO-REMARK\"]", PackageTier.Basic);
        var contact = await CreateContactAsync(client);
        var rule = await CreateRemarkRuleAsync(client);

        using var noEntitlement = await CreateRemarkTaskResponseAsync(client, rule.Id, contact.Id, "no-entitlement");
        await AssertFeatureRequiredAsync(noEntitlement);

        await IssueAndRedeemAsync(
            client,
            "GROUP_ONLY",
            ServiceDurationKind.Days30,
            ServiceTargetKind.Contact,
            contact.Id,
            "wrong-auto-feature");
        using var wrongPackage = await CreateRemarkTaskResponseAsync(client, rule.Id, contact.Id, "wrong-package");
        await AssertFeatureRequiredAsync(wrongPackage);
        var deniedTasks = await client.GetFromJsonAsync<List<RemarkTaskItem>>("/api/remark-tasks", JsonOptions);
        Assert.Empty(deniedTasks!);

        var autoEntitlement = await IssueAndRedeemAsync(
            client,
            "AUTO_UPPER",
            ServiceDurationKind.Days30,
            ServiceTargetKind.Contact,
            contact.Id,
            "correct-auto-feature");
        using var taskResponse = await CreateRemarkTaskResponseAsync(client, rule.Id, contact.Id, "correct-package");
        Assert.Equal(HttpStatusCode.Created, taskResponse.StatusCode);
        var task = await taskResponse.Content.ReadFromJsonAsync<RemarkTaskItem>(JsonOptions);
        Assert.Equal(RemarkTaskStatus.Pending, task!.Status);

        var entitlements = await GetEntitlementsAsync(client, ServiceTargetKind.Contact, contact.Id);
        var auto = Assert.Single(entitlements, x => x.Id == autoEntitlement.EntitlementId);
        var revoke = await client.PatchAsJsonAsync(
            $"/api/entitlements/{auto.Id:D}/state",
            new EntitlementStateRequest(auto.Version, EntitlementState.Revoked, "feature completion test"),
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var completion = await client.PostAsJsonAsync(
            $"/api/remark-tasks/{task.Id:D}/complete",
            new RemarkTaskCompleteRequest(task.Version, true, task.GeneratedRemark, null),
            JsonOptions);
        await AssertFeatureRequiredAsync(completion);
        var unchanged = await client.GetFromJsonAsync<RemarkTaskItem>(
            $"/api/remark-tasks/{task.Id:D}",
            JsonOptions);
        Assert.Equal(RemarkTaskStatus.Pending, unchanged!.Status);

        var audits = await client.GetFromJsonAsync<List<AuditItem>>(
            "/api/audit-logs?action=remark-task.completion-rejected.feature-required",
            JsonOptions);
        Assert.Contains(audits!, x => x.ResourceId == task.Id.ToString("D") && !x.Success);
    }

    [Fact]
    public async Task Auto_remark_preview_requires_its_feature_and_does_not_create_a_task()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        await AddPackageAsync(factory, "PREVIEW_GROUP_ONLY", "[\"group-mention\"]", PackageTier.Basic);
        var contact = await CreateContactAsync(client);
        var rule = await CreateRemarkRuleAsync(client);

        using var noEntitlement = await PreviewRemarkResponseAsync(client, rule.Id, contact.Id);
        await AssertFeatureRequiredAsync(noEntitlement);

        await IssueAndRedeemAsync(
            client,
            "PREVIEW_GROUP_ONLY",
            ServiceDurationKind.Days30,
            ServiceTargetKind.Contact,
            contact.Id,
            "preview-wrong-feature");
        using var wrongFeature = await PreviewRemarkResponseAsync(client, rule.Id, contact.Id);
        await AssertFeatureRequiredAsync(wrongFeature);

        await IssueAndRedeemAsync(
            client,
            "BASIC",
            ServiceDurationKind.Days30,
            ServiceTargetKind.Contact,
            contact.Id,
            "preview-auto-feature");
        using var entitled = await PreviewRemarkResponseAsync(client, rule.Id, contact.Id);
        Assert.Equal(HttpStatusCode.OK, entitled.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<RemarkTaskItem>>("/api/remark-tasks", JsonOptions))!);

        var audits = await client.GetFromJsonAsync<List<AuditItem>>(
            "/api/audit-logs?action=remark-task.preview-rejected.feature-required",
            JsonOptions);
        Assert.Equal(2, audits!.Count(x => x.ResourceId == contact.Id.ToString("D") && !x.Success));
    }

    private static async Task AssertFeatureRequiredAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            "auto_remark_feature_required",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AddPackageAsync(
        TestApplicationFactory factory,
        string code,
        string featuresJson,
        PackageTier tier)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ServicePackages.Add(new ServicePackage
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = $"Test {code}",
            Tier = tier,
            FeaturesJson = featuresJson,
            IsEnabled = true
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Redemption> IssueAndRedeemAsync(
        HttpClient client,
        string packageCode,
        ServiceDurationKind duration,
        ServiceTargetKind targetKind,
        Guid targetId,
        string keySuffix)
    {
        var code = await IssueCodeAsync(client, packageCode, duration);
        using var response = await RedeemResponseAsync(
            client,
            code.Code,
            targetKind,
            targetId,
            $"{keySuffix}-{targetId:N}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<Redemption>(body, JsonOptions)!;
    }

    private static async Task<IssuedCode> IssueCodeAsync(
        HttpClient client,
        string packageCode,
        ServiceDurationKind duration)
    {
        var response = await client.PostAsJsonAsync(
            "/api/activation-codes",
            new IssueActivationCodeRequest(packageCode, duration, null),
            JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<IssuedCode>(body, JsonOptions)!;
    }

    private static async Task<HttpResponseMessage> RedeemResponseAsync(
        HttpClient client,
        string code,
        ServiceTargetKind targetKind,
        Guid targetId,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/activation-codes/redeem")
        {
            Content = JsonContent.Create(new RedeemActivationCodeRequest(code, targetKind, targetId), options: JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<GroupItem> CreateGroupAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/groups",
            new GroupCreateRequest(
                $"entitlement-group-{Guid.NewGuid():N}",
                "Entitlement group",
                "Entitlement tests",
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
                $"entitlement-contact-{Guid.NewGuid():N}",
                "Entitlement contact",
                "wx-entitlement",
                "C-ENT",
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
                $"entitlement-rule-{Guid.NewGuid():N}",
                ServiceTargetKind.Contact,
                "{customerCode}-{displayName}",
                RemarkConflictPolicy.OverwriteSystemGeneratedOnly,
                true,
                64),
            JsonOptions);
        return (await response.Content.ReadFromJsonAsync<RuleItem>(JsonOptions))!;
    }

    private static async Task<MentionItem> PostMentionAsync(HttpClient client, Guid groupId, string eventPrefix)
    {
        var response = await client.PostAsJsonAsync(
            "/api/group-mentions",
            new GroupMentionRequest(
                $"{eventPrefix}-{Guid.NewGuid():N}",
                groupId,
                "member-feature-test",
                "@bot feature test",
                true,
                false,
                DateTimeOffset.UtcNow),
            JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<MentionItem>(body, JsonOptions)!;
    }

    private static async Task<HttpResponseMessage> CreateRemarkTaskResponseAsync(
        HttpClient client,
        Guid ruleId,
        Guid targetId,
        string keyPrefix)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/remark-tasks")
        {
            Content = JsonContent.Create(new RemarkTaskRequest(ruleId, targetId), options: JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", $"{keyPrefix}-{Guid.NewGuid():N}");
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PreviewRemarkResponseAsync(
        HttpClient client,
        Guid ruleId,
        Guid targetId) =>
        client.PostAsJsonAsync(
            "/api/remark-tasks/preview",
            new RemarkTaskRequest(ruleId, targetId),
            JsonOptions);

    private static Task<List<EntitlementItem>> GetEntitlementsAsync(
        HttpClient client,
        ServiceTargetKind targetKind,
        Guid targetId) =>
        client.GetFromJsonAsync<List<EntitlementItem>>(
            $"/api/entitlements?targetKind={targetKind.ToString().ToLowerInvariant()}&targetId={targetId:D}",
            JsonOptions)!;

    private sealed record IssuedCode(Guid Id, string Code);
    private sealed record ActivationSummary(Guid Id, string Status);
    private sealed record Redemption(
        Guid EntitlementId,
        string PackageCode,
        DateTimeOffset StartsAt,
        DateTimeOffset? EndsAt,
        string Status,
        bool Replayed);
    private sealed record EntitlementItem(
        Guid Id,
        string PackageCode,
        DateTimeOffset StartsAt,
        DateTimeOffset? EndsAt,
        string EffectiveStatus,
        long Version);
    private sealed record LedgerItem(string EventType);
    private sealed record GroupItem(Guid Id);
    private sealed record ContactItem(Guid Id);
    private sealed record RuleItem(Guid Id);
    private sealed record MentionItem(
        MentionDecision Decision,
        string DecisionReason,
        Guid? EntitlementId);
    private sealed record RemarkTaskItem(
        Guid Id,
        string GeneratedRemark,
        RemarkTaskStatus Status,
        long Version);
    private sealed record AuditItem(string ResourceId, bool Success);
}
