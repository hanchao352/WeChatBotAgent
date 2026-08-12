using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Services;

namespace WeChatBot.Backend.Tests;

public sealed class BackendHardeningIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task Ef_model_has_no_pending_migration_changes()
    {
        using var factory = new TestApplicationFactory();
        _ = factory.CreateAuthenticatedClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("contacts")]
    [InlineData("groups")]
    [InlineData("remark-rules")]
    [InlineData("group-mentions")]
    public async Task Whitespace_only_identifiers_are_rejected_without_persistence(string resource)
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = resource switch
        {
            "contacts" => await client.PostAsJsonAsync(
                "/api/contacts",
                new ContactCreateRequest(" \t ", "valid", null, null, null, false, null),
                JsonOptions),
            "groups" => await client.PostAsJsonAsync(
                "/api/groups",
                new GroupCreateRequest(" \t ", "valid", null, null, false, null),
                JsonOptions),
            "remark-rules" => await client.PostAsJsonAsync(
                "/api/remark-rules",
                new RemarkRuleCreateRequest(
                    " \t ",
                    ServiceTargetKind.Contact,
                    "{displayName}",
                    RemarkConflictPolicy.Skip,
                    true),
                JsonOptions),
            "group-mentions" => await PostWhitespaceMentionAsync(client),
            _ => throw new ArgumentOutOfRangeException(nameof(resource))
        };
        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("validation_failed", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Advanced_feature_requires_an_active_basic_entitlement_for_the_same_target()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client);

        var advancedCode = await IssueCodeAsync(client, "ADVANCED_GENERAL");
        using var rejected = await RedeemAsync(
            client,
            advancedCode.Code,
            group.Id,
            "advanced-without-basic");
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains(
            "advanced_package_requires_basic",
            await rejected.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
        var activationCodes = await client.GetFromJsonAsync<List<ActivationCodeItem>>("/api/activation-codes", JsonOptions);
        Assert.Equal("available", Assert.Single(activationCodes!, x => x.Id == advancedCode.Id).Status);

        var basic = await IssueAndRedeemAsync(
            client,
            "BASIC",
            ServiceTargetKind.Group,
            group.Id,
            "advanced-with-basic");
        using var advancedResponse = await RedeemAsync(
            client,
            advancedCode.Code,
            group.Id,
            "advanced-after-basic");
        var advancedBody = await advancedResponse.Content.ReadAsStringAsync();
        Assert.True(advancedResponse.IsSuccessStatusCode, advancedBody);
        var advanced = JsonSerializer.Deserialize<Redemption>(advancedBody, JsonOptions)!;
        var withBasic = await PostMentionAsync(client, group.Id, "advanced-with-active-basic");
        Assert.Equal(MentionDecision.Accepted, withBasic.Decision);

        var suspend = await client.PatchAsJsonAsync(
            $"/api/entitlements/{basic.EntitlementId:D}/state",
            new EntitlementStateRequest(1, EntitlementState.Suspended, "dependency regression test"),
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        var suspendedBasic = await PostMentionAsync(client, group.Id, "advanced-with-suspended-basic");
        Assert.Equal(MentionDecision.ActivationRequired, suspendedBasic.Decision);

        var resume = await client.PatchAsJsonAsync(
            $"/api/entitlements/{basic.EntitlementId:D}/state",
            new EntitlementStateRequest(2, EntitlementState.Active, "dependency regression test"),
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        Assert.NotEqual(Guid.Empty, advanced.EntitlementId);
    }

    [Fact]
    public async Task Direct_service_activation_is_atomic_and_idempotent()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client);
        var key = $"direct-{Guid.NewGuid():N}";
        var request = new ActivateServiceRequest(
            "BASIC",
            ServiceDurationKind.Days60,
            ServiceTargetKind.Group,
            group.Id);

        async Task<HttpResponseMessage> ActivateAsync(ActivateServiceRequest body)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/entitlements/activate")
            {
                Content = JsonContent.Create(body, options: JsonOptions)
            };
            message.Headers.Add("Idempotency-Key", key);
            return await client.SendAsync(message);
        }

        using var first = await ActivateAsync(request);
        using var replay = await ActivateAsync(request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var firstResult = await first.Content.ReadFromJsonAsync<Redemption>(JsonOptions);
        var replayResult = await replay.Content.ReadFromJsonAsync<Redemption>(JsonOptions);
        Assert.Equal(firstResult!.EntitlementId, replayResult!.EntitlementId);
        Assert.True(replayResult.Replayed);

        using var conflict = await ActivateAsync(request with { Duration = ServiceDurationKind.Days90 });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("idempotency_key_reused", await conflict.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await db.Entitlements.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TargetId == group.Id && x.Source == "admin-direct")
            .ToListAsync());
        Assert.Single(await db.EntitlementLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.EntitlementId == firstResult.EntitlementId)
            .ToListAsync());
        Assert.False(await db.ActivationCodes.IgnoreQueryFilters().AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task Direct_advanced_activation_requires_an_active_basic_entitlement()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client);
        var request = new ActivateServiceRequest(
            "ADVANCED_GENERAL",
            ServiceDurationKind.Days30,
            ServiceTargetKind.Group,
            group.Id);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/entitlements/activate")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        message.Headers.Add("Idempotency-Key", $"direct-advanced-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("advanced_package_requires_basic", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Entitlements.IgnoreQueryFilters().AsNoTracking().AnyAsync(x => x.TargetId == group.Id));
    }

    [Fact]
    public async Task Audit_read_fails_closed_after_database_tampering()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        _ = await CreateGroupAsync(client);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().OrderByDescending(x => x.CreatedAt).FirstAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE AuditLogs SET Actor = {"forged-actor"} WHERE Id = {audit.Id}");
        }

        using var response = await client.GetAsync("/api/audit-logs");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("audit_integrity_failed", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Audit_integrity_covers_the_captured_ip_address()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        _ = await CreateGroupAsync(client);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditService = scope.ServiceProvider.GetRequiredService<AuditService>();
        var stored = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .FirstAsync();
        Assert.True(auditService.HasValidIntegrity(stored));

        stored.IpAddress = "203.0.113.10";
        Assert.False(auditService.HasValidIntegrity(stored));
    }

    [Fact]
    public async Task Audit_integrity_does_not_allow_delimiter_boundary_shifting()
    {
        using var factory = new TestApplicationFactory();
        _ = factory.CreateAuthenticatedClient();
        using var scope = factory.Services.CreateScope();
        var auditService = scope.ServiceProvider.GetRequiredService<AuditService>();
        var entry = new AuditLog
        {
            Id = Guid.NewGuid(),
            TenantId = TestApplicationFactory.TenantId,
            CreatedAt = DateTimeOffset.UtcNow,
            Actor = "integration-test-admin",
            Action = "audit.boundary-test",
            ResourceType = "AuditLog",
            ResourceId = Guid.NewGuid().ToString("D"),
            Success = true,
            CorrelationId = "correlation",
            DetailsJson = "left|right"
        };
        entry.IntegrityHash = auditService.ComputeIntegrityHash(entry);
        Assert.True(auditService.HasValidIntegrity(entry));

        entry.CorrelationId = $"{entry.CorrelationId}|left";
        entry.DetailsJson = "right";

        Assert.False(auditService.HasValidIntegrity(entry));
    }

    [Fact]
    public async Task Legacy_audit_hash_is_not_accepted_when_the_record_has_an_ip_address()
    {
        using var factory = new TestApplicationFactory();
        _ = factory.CreateAuthenticatedClient();
        using var scope = factory.Services.CreateScope();
        var auditService = scope.ServiceProvider.GetRequiredService<AuditService>();
        var now = DateTimeOffset.UtcNow;
        var entry = new AuditLog
        {
            Id = Guid.NewGuid(),
            TenantId = TestApplicationFactory.TenantId,
            CreatedAt = now,
            Actor = "legacy-actor",
            Action = "legacy.action",
            ResourceType = "LegacyResource",
            ResourceId = Guid.NewGuid().ToString("D"),
            Success = true,
            CorrelationId = "legacy-correlation",
            DetailsJson = "{}"
        };
        entry.IntegrityHash = ComputeLegacyAuditHash(entry);
        Assert.True(auditService.HasValidIntegrity(entry));

        entry.IpAddress = "198.51.100.4";
        Assert.False(auditService.HasValidIntegrity(entry));
    }

    [Fact]
    public async Task Legacy_audit_records_are_upgraded_without_trusting_the_unsigned_ip_address()
    {
        using var factory = new TestApplicationFactory();
        _ = factory.CreateAuthenticatedClient();
        var legacyId = Guid.NewGuid();
        var tamperedId = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditService = scope.ServiceProvider.GetRequiredService<AuditService>();
        var legacy = CreateLegacyAudit(legacyId, "legacy-actor", "198.51.100.4");
        var tampered = CreateLegacyAudit(tamperedId, "original-actor", "203.0.113.9");
        db.AuditLogs.AddRange(legacy, tampered);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE AuditLogs SET Actor = {"forged-actor"} WHERE Id = {tamperedId}");
        db.ChangeTracker.Clear();

        Assert.Equal(1, await DbInitializer.UpgradeLegacyAuditIntegrityAsync(
            db, auditService, TestApplicationFactory.TenantId));
        Assert.Equal(0, await DbInitializer.UpgradeLegacyAuditIntegrityAsync(
            db, auditService, TestApplicationFactory.TenantId));

        var upgraded = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == legacyId);
        Assert.Null(upgraded.IpAddress);
        Assert.True(auditService.HasCurrentIntegrity(upgraded));

        var rejected = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == tamperedId);
        Assert.Equal("203.0.113.9", rejected.IpAddress);
        Assert.False(auditService.HasValidIntegrity(rejected));
    }

    [Fact]
    public async Task Previous_audit_records_are_upgraded_while_preserving_the_signed_ip_address()
    {
        using var factory = new TestApplicationFactory();
        _ = factory.CreateAuthenticatedClient();
        var previousId = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditService = scope.ServiceProvider.GetRequiredService<AuditService>();
        var previous = new AuditLog
        {
            Id = previousId,
            TenantId = TestApplicationFactory.TenantId,
            CreatedAt = DateTimeOffset.UtcNow,
            Actor = "previous-actor",
            Action = "previous.action",
            ResourceType = "PreviousResource",
            ResourceId = Guid.NewGuid().ToString("D"),
            Success = true,
            IpAddress = "198.51.100.42",
            CorrelationId = $"previous-{previousId:N}",
            DetailsJson = "{}"
        };
        previous.IntegrityHash = ComputePreviousAuditHash(previous);
        db.AuditLogs.Add(previous);
        await db.SaveChangesAsync();

        Assert.Equal(1, await DbInitializer.UpgradeLegacyAuditIntegrityAsync(
            db, auditService, TestApplicationFactory.TenantId));

        var upgraded = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == previousId);
        Assert.Equal("198.51.100.42", upgraded.IpAddress);
        Assert.True(auditService.HasCurrentIntegrity(upgraded));
    }

    [Fact]
    public async Task Mention_event_id_is_normalized_before_duplicate_lookup()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client);
        var capturedAt = DateTimeOffset.UtcNow;
        var eventId = $"normalized-mention-{Guid.NewGuid():N}";
        var firstRequest = new GroupMentionRequest(
            $"  {eventId}  ", group.Id, "member", "@bot", true, false, capturedAt);
        var replayRequest = firstRequest with { ExternalEventId = eventId };

        using var first = await client.PostAsJsonAsync("/api/group-mentions", firstRequest, JsonOptions);
        using var replay = await client.PostAsJsonAsync("/api/group-mentions", replayRequest, JsonOptions);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var firstResult = await first.Content.ReadFromJsonAsync<MentionResponse>(JsonOptions);
        var replayResult = await replay.Content.ReadFromJsonAsync<MentionResponse>(JsonOptions);
        Assert.Equal(firstResult!.Id, replayResult!.Id);
        Assert.True(replayResult.Duplicate);
    }

    [Fact]
    public async Task Restore_rejects_a_backup_containing_a_tampered_audit_record()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        _ = await CreateGroupAsync(client);
        var backupResponse = await client.PostAsJsonAsync(
            "/api/backups",
            new CreateBackupRequest("audit integrity regression"),
            JsonOptions);
        var backupBody = await backupResponse.Content.ReadAsStringAsync();
        Assert.True(backupResponse.IsSuccessStatusCode, backupBody);
        var backup = JsonSerializer.Deserialize<BackupItem>(backupBody, JsonOptions)!;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var manifest = await db.BackupManifests.IgnoreQueryFilters().SingleAsync(x => x.Id == backup.Id);
            var path = Path.Combine(factory.BackupDirectory, manifest.FileName);
            var encrypted = await File.ReadAllBytesAsync(path);
            var key = SHA256.HashData("integration-test-backup-key"u8.ToArray());
            var plaintext = DecryptBackup(encrypted, key);
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement.Clone();
            var auditLogs = root.GetProperty("auditLogs").EnumerateArray().Select(x => x.Clone()).ToList();
            Assert.NotEmpty(auditLogs);

            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.NameEquals("auditLogs"))
                    {
                        writer.WriteStartArray();
                        var tampered = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(auditLogs[0].GetRawText())!;
                        tampered["actor"] = JsonSerializer.SerializeToElement("forged-backup-actor");
                        JsonSerializer.Serialize(writer, tampered);
                        foreach (var item in auditLogs.Skip(1)) item.WriteTo(writer);
                        writer.WriteEndArray();
                    }
                    else
                    {
                        property.Value.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }

            var tamperedEncrypted = EncryptBackup(output.ToArray(), key);
            await File.WriteAllBytesAsync(path, tamperedEncrypted);
            manifest.PayloadSha256 = StableHash.Sha256(tamperedEncrypted);
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/backups/{backup.Id:D}/restore")
        {
            Content = JsonContent.Create(new RestoreBackupRequest("RESTORE"), options: JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", $"tampered-audit-{Guid.NewGuid():N}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            "backup_audit_integrity_failed",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
        var state = await client.GetFromJsonAsync<SystemStateItem>("/api/system-state", JsonOptions);
        Assert.False(state!.AutomationPaused);
    }

    [Fact]
    public async Task Verify_rejects_a_backup_containing_a_tampered_audit_record()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        _ = await CreateGroupAsync(client);
        var backup = await CreateBackupAsync(client, "verify audit integrity regression");
        await RewriteBackupAsync(factory, backup.Id, (root, writer) =>
        {
            foreach (var property in root.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.NameEquals("auditLogs"))
                {
                    writer.WriteStartArray();
                    var auditLogs = property.Value.EnumerateArray().ToList();
                    Assert.NotEmpty(auditLogs);
                    var tampered = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(auditLogs[0].GetRawText())!;
                    tampered["actor"] = JsonSerializer.SerializeToElement("forged-verify-actor");
                    JsonSerializer.Serialize(writer, tampered);
                    foreach (var item in auditLogs.Skip(1)) item.WriteTo(writer);
                    writer.WriteEndArray();
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }
        });

        using var response = await client.PostAsync($"/api/backups/{backup.Id:D}/verify", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var verification = await response.Content.ReadFromJsonAsync<BackupVerification>(JsonOptions);
        Assert.False(verification!.IsValid);
    }

    [Fact]
    public async Task Restore_accepts_a_backup_with_the_previous_valid_audit_hash_format()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        _ = await CreateGroupAsync(client);
        var backup = await CreateBackupAsync(client, "previous audit hash compatibility");
        await RewriteBackupAsync(factory, backup.Id, (root, writer) =>
        {
            foreach (var property in root.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.NameEquals("auditLogs"))
                {
                    writer.WriteStartArray();
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        var auditLog = JsonSerializer.Deserialize<AuditLog>(item.GetRawText(), JsonOptions)!;
                        auditLog.IntegrityHash = ComputePreviousAuditHash(auditLog);
                        JsonSerializer.Serialize(writer, auditLog, JsonOptions);
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/backups/{backup.Id:D}/restore")
        {
            Content = JsonContent.Create(new RestoreBackupRequest("RESTORE"), options: JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", $"previous-audit-{Guid.NewGuid():N}");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Restore_rejects_child_records_that_reference_a_foreign_tenant_parent()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client);
        var backup = await CreateBackupAsync(client, "foreign parent regression");
        var foreignGroupId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Groups
                    (Id, TenantId, ExternalId, DisplayName, ManualRemarkProtected, CreatedAt, UpdatedAt, Version)
                VALUES
                    ({foreignGroupId}, {Guid.NewGuid()}, {"foreign-parent"}, {"Foreign parent"}, {false}, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow}, {1})
                """);
        }

        await RewriteBackupAsync(factory, backup.Id, (root, writer) =>
        {
            foreach (var property in root.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.NameEquals("groupMentions"))
                {
                    writer.WriteStartArray();
                    JsonSerializer.Serialize(writer, new
                    {
                        id = Guid.NewGuid(),
                        tenantId = TestApplicationFactory.TenantId,
                        externalEventId = $"foreign-parent-{Guid.NewGuid():N}",
                        groupId = foreignGroupId,
                        senderExternalId = "member",
                        content = "@bot",
                        mentionedBot = true,
                        senderIsBot = false,
                        capturedAt = DateTimeOffset.UtcNow,
                        decision = MentionDecision.ActivationRequired,
                        decisionReason = "test",
                        entitlementId = (Guid?)null,
                        createdAt = DateTimeOffset.UtcNow
                    }, JsonOptions);
                    writer.WriteEndArray();
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }
        });
        await UpdateManifestCountsAsync(factory, backup.Id, "groupMentions", 1);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/backups/{backup.Id:D}/restore")
        {
            Content = JsonContent.Create(new RestoreBackupRequest("RESTORE"), options: JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", $"foreign-parent-{Guid.NewGuid():N}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            "backup_reference_integrity_failed",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        using var scopeAfter = factory.Services.CreateScope();
        var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await dbAfter.GroupMentions.IgnoreQueryFilters().AnyAsync(x => x.GroupId == foreignGroupId));
        Assert.True(await dbAfter.Groups.IgnoreQueryFilters().AnyAsync(x => x.Id == group.Id));
    }

    [Fact]
    public async Task Restore_rejects_entitlements_that_reference_a_missing_target()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var backup = await CreateBackupAsync(client, "missing target regression");
        var missingGroupId = Guid.NewGuid();

        await RewriteBackupAsync(factory, backup.Id, (root, writer) =>
        {
            foreach (var property in root.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.NameEquals("entitlements"))
                {
                    writer.WriteStartArray();
                    JsonSerializer.Serialize(writer, new Entitlement
                    {
                        Id = Guid.NewGuid(),
                        TenantId = TestApplicationFactory.TenantId,
                        TargetKind = ServiceTargetKind.Group,
                        TargetId = missingGroupId,
                        PackageId = WellKnownPackages.BasicId,
                        DurationKind = ServiceDurationKind.Days30,
                        StartsAt = DateTimeOffset.UtcNow,
                        EndsAt = DateTimeOffset.UtcNow.AddDays(30),
                        State = EntitlementState.Active,
                        Source = "tampered-backup",
                        CreatedAt = DateTimeOffset.UtcNow
                    }, JsonOptions);
                    writer.WriteEndArray();
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }
        });
        await UpdateManifestCountsAsync(factory, backup.Id, "entitlements", 1);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/backups/{backup.Id:D}/restore")
        {
            Content = JsonContent.Create(new RestoreBackupRequest("RESTORE"), options: JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", $"missing-target-{Guid.NewGuid():N}");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            "backup_reference_integrity_failed",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_rejects_mentions_that_reference_a_missing_entitlement()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client);
        var backup = await CreateBackupAsync(client, "missing mention entitlement regression");

        await RewriteBackupAsync(factory, backup.Id, (root, writer) =>
        {
            foreach (var property in root.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.NameEquals("groupMentions"))
                {
                    writer.WriteStartArray();
                    JsonSerializer.Serialize(writer, new GroupMentionEvent
                    {
                        Id = Guid.NewGuid(),
                        TenantId = TestApplicationFactory.TenantId,
                        ExternalEventId = $"missing-entitlement-{Guid.NewGuid():N}",
                        GroupId = group.Id,
                        SenderExternalId = "member",
                        Content = "@bot",
                        MentionedBot = true,
                        CapturedAt = DateTimeOffset.UtcNow,
                        Decision = MentionDecision.Accepted,
                        DecisionReason = "tampered-backup",
                        EntitlementId = Guid.NewGuid(),
                        CreatedAt = DateTimeOffset.UtcNow
                    }, JsonOptions);
                    writer.WriteEndArray();
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }
        });
        await UpdateManifestCountsAsync(factory, backup.Id, "groupMentions", 1);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/backups/{backup.Id:D}/restore")
        {
            Content = JsonContent.Create(new RestoreBackupRequest("RESTORE"), options: JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", $"missing-entitlement-{Guid.NewGuid():N}");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            "backup_reference_integrity_failed",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Advanced_general_activation_rejects_contact_targets_without_consuming_the_code()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var contactResponse = await client.PostAsJsonAsync(
            "/api/contacts",
            new ContactCreateRequest($"advanced-contact-{Guid.NewGuid():N}", "Advanced contact", null, null, null, false, null),
            JsonOptions);
        var contact = await contactResponse.Content.ReadFromJsonAsync<ContactItem>(JsonOptions);
        var issued = await IssueCodeAsync(client, "ADVANCED_GENERAL");

        using var response = await RedeemAsync(
            client,
            issued.Code,
            contact!.Id,
            $"advanced-contact-{Guid.NewGuid():N}",
            ServiceTargetKind.Contact);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("advanced_package_requires_group", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var activationCodes = await client.GetFromJsonAsync<List<ActivationCodeItem>>("/api/activation-codes", JsonOptions);
        Assert.Equal("available", Assert.Single(activationCodes!, x => x.Id == issued.Id).Status);
    }

    [Fact]
    public async Task Save_changes_rejects_cross_tenant_entities_even_when_query_filters_are_bypassed()
    {
        using var factory = new TestApplicationFactory();
        _ = factory.CreateAuthenticatedClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Contacts.Add(new Contact
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            ExternalId = "cross-tenant",
            DisplayName = "Cross tenant",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("authenticated tenant", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_changes_rejects_cross_tenant_deletes_when_query_filters_are_bypassed()
    {
        using var factory = new TestApplicationFactory();
        _ = factory.CreateAuthenticatedClient();
        var foreignContactId = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Contacts
                (Id, TenantId, ExternalId, DisplayName, ManualRemarkProtected, CreatedAt, UpdatedAt, Version)
            VALUES
                ({foreignContactId}, {Guid.NewGuid()}, {"foreign-delete"}, {"Foreign delete"}, {false}, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow}, {1})
            """);
        var foreignContact = await db.Contacts.IgnoreQueryFilters()
            .SingleAsync(x => x.Id == foreignContactId);
        db.Contacts.Remove(foreignContact);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("authenticated tenant", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await db.Contacts.IgnoreQueryFilters().AsNoTracking().AnyAsync(x => x.Id == foreignContactId));
    }

    [Fact]
    public async Task Save_changes_rejects_reassigning_a_foreign_record_to_the_authenticated_tenant()
    {
        using var factory = new TestApplicationFactory();
        _ = factory.CreateAuthenticatedClient();
        var foreignTenantId = Guid.NewGuid();
        var foreignContactId = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Contacts
                (Id, TenantId, ExternalId, DisplayName, ManualRemarkProtected, CreatedAt, UpdatedAt, Version)
            VALUES
                ({foreignContactId}, {foreignTenantId}, {"foreign-reassign"}, {"Foreign reassign"}, {false}, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow}, {1})
            """);
        var foreignContact = await db.Contacts.IgnoreQueryFilters()
            .SingleAsync(x => x.Id == foreignContactId);
        foreignContact.TenantId = TestApplicationFactory.TenantId;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("authenticated tenant", exception.Message, StringComparison.OrdinalIgnoreCase);
        var unchangedTenant = await db.Contacts.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == foreignContactId)
            .Select(x => x.TenantId)
            .SingleAsync();
        Assert.Equal(foreignTenantId, unchangedTenant);
    }

    [Fact]
    public async Task Idempotency_keys_are_compared_after_header_whitespace_is_normalized()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client);
        var issued = await IssueCodeAsync(client, "BASIC");
        var key = $"normalized-{Guid.NewGuid():N}";

        using var first = await RedeemAsync(client, issued.Code, group.Id, $"  {key}  ");
        using var replay = await RedeemAsync(client, issued.Code, group.Id, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var firstResult = await first.Content.ReadFromJsonAsync<Redemption>(JsonOptions);
        var replayResult = await replay.Content.ReadFromJsonAsync<Redemption>(JsonOptions);
        Assert.Equal(firstResult!.EntitlementId, replayResult!.EntitlementId);
        Assert.True(replayResult.Replayed);
    }

    [Fact]
    public async Task Active_only_filter_is_applied_before_the_result_limit()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client);
        var active = await IssueAndRedeemAsync(
            client,
            "BASIC",
            ServiceTargetKind.Group,
            group.Id,
            "active-filter");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var packageId = await db.ServicePackages
                .Where(x => x.Code == "BASIC")
                .Select(x => x.Id)
                .SingleAsync();
            var now = DateTimeOffset.UtcNow;
            db.Entitlements.AddRange(Enumerable.Range(0, 501).Select(index => new Entitlement
            {
                Id = Guid.NewGuid(),
                TenantId = TestApplicationFactory.TenantId,
                TargetKind = ServiceTargetKind.Group,
                TargetId = group.Id,
                PackageId = packageId,
                DurationKind = ServiceDurationKind.Days30,
                StartsAt = now.AddDays(-60),
                EndsAt = now.AddDays(-30),
                State = EntitlementState.Active,
                Source = "expired-regression-fixture",
                CreatedAt = now.AddMinutes(index + 1)
            }));
            await db.SaveChangesAsync();
        }

        var results = await client.GetFromJsonAsync<List<EntitlementListItem>>(
            $"/api/entitlements?targetKind=group&targetId={group.Id:D}&activeOnly=true",
            JsonOptions);

        var result = Assert.Single(results!);
        Assert.Equal(active.EntitlementId, result.Id);
        Assert.Equal("active", result.EffectiveStatus);
    }

    [Fact]
    public async Task Successful_remark_completion_is_rejected_while_automation_is_paused()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client);
        await IssueAndRedeemAsync(client, "BASIC", ServiceTargetKind.Group, group.Id, "remark-gate");
        using var ruleResponse = await client.PostAsJsonAsync(
            "/api/remark-rules",
            new RemarkRuleCreateRequest(
                $"hardening-rule-{Guid.NewGuid():N}",
                ServiceTargetKind.Group,
                "{displayName}",
                RemarkConflictPolicy.Skip,
                true),
            JsonOptions);
        var rule = await ruleResponse.Content.ReadFromJsonAsync<RuleItem>(JsonOptions);
        using var taskRequest = new HttpRequestMessage(HttpMethod.Post, "/api/remark-tasks")
        {
            Content = JsonContent.Create(new RemarkTaskRequest(rule!.Id, group.Id), options: JsonOptions)
        };
        taskRequest.Headers.Add("Idempotency-Key", $"remark-{Guid.NewGuid():N}");
        using var taskResponse = await client.SendAsync(taskRequest);
        var task = await taskResponse.Content.ReadFromJsonAsync<RemarkTaskItem>(JsonOptions);
        var state = await client.GetFromJsonAsync<SystemStateItem>("/api/system-state", JsonOptions);
        using var pauseResponse = await client.PutAsJsonAsync(
            "/api/system-state/automation",
            new AutomationStateRequest(state!.Version, true, "completion gate test"),
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, pauseResponse.StatusCode);

        using var completion = await client.PostAsJsonAsync(
            $"/api/remark-tasks/{task!.Id:D}/complete",
            new RemarkTaskCompleteRequest(task.Version, true, task.GeneratedRemark, null),
            JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, completion.StatusCode);
        Assert.Contains("automation_paused", await completion.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        var unchanged = await client.GetFromJsonAsync<RemarkTaskItem>($"/api/remark-tasks/{task.Id:D}", JsonOptions);
        Assert.Equal(RemarkTaskStatus.Pending, unchanged!.Status);
    }

    [Fact]
    public async Task Entitlement_state_change_requires_a_meaningful_reason()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var group = await CreateGroupAsync(client);
        var entitlement = await IssueAndRedeemAsync(
            client,
            "BASIC",
            ServiceTargetKind.Group,
            group.Id,
            "state-reason");

        using var response = await client.PatchAsJsonAsync(
            $"/api/entitlements/{entitlement.EntitlementId:D}/state",
            new EntitlementStateRequest(1, EntitlementState.Suspended, " "),
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/swagger")]
    [InlineData("/swagger/")]
    [InlineData("/swagger/index.html")]
    [InlineData("/swagger/v1/swagger.json")]
    public async Task Production_does_not_expose_swagger(string path)
    {
        using var factory = new ProductionApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("openapi", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("production-admin", "production-admin")]
    [InlineData("Production-Admin", "production-admin")]
    [InlineData(" production-admin ", "PRODUCTION-ADMIN")]
    public async Task Production_rejects_ambiguous_admin_and_agent_actor_names(
        string adminActor,
        string agentActor)
    {
        using var factory = new ProductionApplicationFactory(new Dictionary<string, string?>
        {
            ["Auth:ActorName"] = adminActor,
            ["Auth:AgentActorName"] = agentActor
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var client = factory.CreateClient();
            _ = await client.GetAsync("/health/live");
        });
        Assert.Contains("different actors", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("production-admin|forged", "production-agent")]
    [InlineData("production-admin", "production-agent|forged")]
    public async Task Production_rejects_actor_names_unsafe_for_legacy_audit_migration(
        string adminActor,
        string agentActor)
    {
        using var factory = new ProductionApplicationFactory(new Dictionary<string, string?>
        {
            ["Auth:ActorName"] = adminActor,
            ["Auth:AgentActorName"] = agentActor
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var client = factory.CreateClient();
            _ = await client.GetAsync("/health/live");
        });
        Assert.Contains("cannot contain '|'", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<HttpResponseMessage> PostWhitespaceMentionAsync(HttpClient client)
    {
        var group = await CreateGroupAsync(client);
        return await client.PostAsJsonAsync(
            "/api/group-mentions",
            new GroupMentionRequest(" \t ", group.Id, "member", "@bot", true, false, DateTimeOffset.UtcNow),
            JsonOptions);
    }

    private static byte[] DecryptBackup(byte[] encrypted, byte[] key)
    {
        ReadOnlySpan<byte> magic = "WXB1"u8;
        const int nonceLength = 12;
        const int tagLength = 16;
        var nonce = encrypted.AsSpan(magic.Length, nonceLength);
        var tag = encrypted.AsSpan(magic.Length + nonceLength, tagLength);
        var ciphertext = encrypted.AsSpan(magic.Length + nonceLength + tagLength);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, tagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, magic);
        return plaintext;
    }

    private static byte[] EncryptBackup(byte[] plaintext, byte[] key)
    {
        ReadOnlySpan<byte> magic = "WXB1"u8;
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, magic);
        var output = new byte[magic.Length + nonce.Length + tag.Length + ciphertext.Length];
        magic.CopyTo(output);
        nonce.CopyTo(output, magic.Length);
        tag.CopyTo(output, magic.Length + nonce.Length);
        ciphertext.CopyTo(output, magic.Length + nonce.Length + tag.Length);
        return output;
    }

    private static string ComputeLegacyAuditHash(AuditLog entry) => StableHash.HmacSha256(
        $"{entry.Id:N}|{entry.TenantId:N}|{entry.CreatedAt:O}|{entry.Actor}|{entry.Action}|{entry.ResourceType}|{entry.ResourceId}|{entry.Success}|{entry.CorrelationId}|{entry.DetailsJson}",
        "integration-test-audit-integrity-key-32-characters-minimum");

    private static string ComputePreviousAuditHash(AuditLog entry) => StableHash.HmacSha256(
        $"{entry.Id:N}|{entry.TenantId:N}|{entry.CreatedAt:O}|{entry.Actor}|{entry.Action}|{entry.ResourceType}|{entry.ResourceId}|{entry.Success}|{entry.IpAddress ?? string.Empty}|{entry.CorrelationId}|{entry.DetailsJson}",
        "integration-test-audit-integrity-key-32-characters-minimum");

    private static AuditLog CreateLegacyAudit(Guid id, string actor, string? ipAddress)
    {
        var entry = new AuditLog
        {
            Id = id,
            TenantId = TestApplicationFactory.TenantId,
            CreatedAt = DateTimeOffset.UtcNow,
            Actor = actor,
            Action = "legacy.action",
            ResourceType = "LegacyResource",
            ResourceId = Guid.NewGuid().ToString("D"),
            Success = true,
            IpAddress = ipAddress,
            CorrelationId = $"legacy-{id:N}",
            DetailsJson = "{}"
        };
        entry.IntegrityHash = ComputeLegacyAuditHash(entry);
        return entry;
    }

    private static async Task<BackupItem> CreateBackupAsync(HttpClient client, string reason)
    {
        var response = await client.PostAsJsonAsync("/api/backups", new CreateBackupRequest(reason), JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<BackupItem>(body, JsonOptions)!;
    }

    private static async Task RewriteBackupAsync(
        TestApplicationFactory factory,
        Guid backupId,
        Action<JsonElement, Utf8JsonWriter> writeProperties)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manifest = await db.BackupManifests.IgnoreQueryFilters().SingleAsync(x => x.Id == backupId);
        var path = Path.Combine(factory.BackupDirectory, manifest.FileName);
        var key = SHA256.HashData("integration-test-backup-key"u8.ToArray());
        var plaintext = DecryptBackup(await File.ReadAllBytesAsync(path), key);
        using var document = JsonDocument.Parse(plaintext);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writeProperties(document.RootElement, writer);
            writer.WriteEndObject();
        }

        var encrypted = EncryptBackup(output.ToArray(), key);
        await File.WriteAllBytesAsync(path, encrypted);
        manifest.PayloadSha256 = StableHash.Sha256(encrypted);
        await db.SaveChangesAsync();
    }

    private static async Task UpdateManifestCountsAsync(
        TestApplicationFactory factory,
        Guid backupId,
        string name,
        int count)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manifest = await db.BackupManifests.IgnoreQueryFilters().SingleAsync(x => x.Id == backupId);
        var counts = JsonSerializer.Deserialize<Dictionary<string, int>>(manifest.CountsJson, JsonOptions)!;
        counts[name] = count;
        manifest.CountsJson = JsonSerializer.Serialize(counts, JsonOptions);
        await db.SaveChangesAsync();
    }

    private static async Task<GroupItem> CreateGroupAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/groups",
            new GroupCreateRequest(
                $"hardening-group-{Guid.NewGuid():N}",
                "Hardening group",
                null,
                null,
                false,
                null),
            JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<GroupItem>(body, JsonOptions)!;
    }

    private static async Task<Redemption> IssueAndRedeemAsync(
        HttpClient client,
        string packageCode,
        ServiceTargetKind targetKind,
        Guid targetId,
        string keyPrefix)
    {
        var issued = await IssueCodeAsync(client, packageCode);
        using var response = await RedeemAsync(
            client,
            issued.Code,
            targetId,
            $"{keyPrefix}-{Guid.NewGuid():N}",
            targetKind);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<Redemption>(body, JsonOptions)!;
    }

    private static async Task<IssuedCode> IssueCodeAsync(HttpClient client, string packageCode)
    {
        var response = await client.PostAsJsonAsync(
            "/api/activation-codes",
            new IssueActivationCodeRequest(packageCode, ServiceDurationKind.Days30, null),
            JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<IssuedCode>(body, JsonOptions)!;
    }

    private static Task<HttpResponseMessage> RedeemAsync(
        HttpClient client,
        string code,
        Guid targetId,
        string idempotencyKey,
        ServiceTargetKind targetKind = ServiceTargetKind.Group)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/activation-codes/redeem")
        {
            Content = JsonContent.Create(
                new RedeemActivationCodeRequest(code, targetKind, targetId),
                options: JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }

    private static async Task<MentionItem> PostMentionAsync(HttpClient client, Guid groupId, string prefix)
    {
        var response = await client.PostAsJsonAsync(
            "/api/group-mentions",
            new GroupMentionRequest(
                $"{prefix}-{Guid.NewGuid():N}",
                groupId,
                "hardening-member",
                "@bot hardening test",
                true,
                false,
                DateTimeOffset.UtcNow),
            JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<MentionItem>(body, JsonOptions)!;
    }

    private sealed record GroupItem(Guid Id);
    private sealed record ContactItem(Guid Id);
    private sealed record IssuedCode(Guid Id, string Code);
    private sealed record Redemption(Guid EntitlementId, bool Replayed);
    private sealed record MentionItem(MentionDecision Decision);
    private sealed record MentionResponse(Guid Id, bool Duplicate);
    private sealed record BackupItem(Guid Id);
    private sealed record ActivationCodeItem(Guid Id, string Status);
    private sealed record EntitlementListItem(Guid Id, string EffectiveStatus);
    private sealed record RuleItem(Guid Id);
    private sealed record RemarkTaskItem(
        Guid Id,
        string GeneratedRemark,
        RemarkTaskStatus Status,
        long Version);
    private sealed record SystemStateItem(long Version, bool AutomationPaused);

    private sealed class ProductionApplicationFactory(
        IReadOnlyDictionary<string, string?>? overrides = null) : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "wechatbot-backend-production-tests",
            Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_root);
            builder.UseEnvironment("Production");
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = $"Data Source={Path.Combine(_root, "production.db")};Default Timeout=30;Pooling=False",
                ["Auth:ApiKey"] = "production-admin-api-key-with-more-than-thirty-two-characters",
                ["Auth:AgentApiKey"] = "production-agent-api-key-with-more-than-thirty-two-characters",
                ["Auth:TenantId"] = Guid.NewGuid().ToString("D"),
                ["Auth:ActorName"] = "production-admin",
                ["Auth:AgentActorName"] = "production-agent",
                ["Activation:HashPepper"] = "production-activation-pepper-with-more-than-thirty-two-characters",
                ["Audit:IntegrityKey"] = "production-audit-integrity-key-with-more-than-thirty-two-characters",
                ["Backup:Directory"] = Path.Combine(_root, "backups"),
                ["Backup:EncryptionKeyBase64"] = Convert.ToBase64String(SHA256.HashData("production-backup-key"u8.ToArray()))
            };
            if (overrides is not null)
            {
                foreach (var pair in overrides) values[pair.Key] = pair.Value;
            }
            builder.UseSetting("ConnectionStrings:Database", values["ConnectionStrings:Database"]);
            builder.UseSetting("Auth:ApiKey", values["Auth:ApiKey"]);
            builder.UseSetting("Auth:AgentApiKey", values["Auth:AgentApiKey"]);
            builder.UseSetting("Auth:TenantId", values["Auth:TenantId"]);
            builder.UseSetting("Auth:ActorName", values["Auth:ActorName"]);
            builder.UseSetting("Auth:AgentActorName", values["Auth:AgentActorName"]);
            builder.UseSetting("Activation:HashPepper", values["Activation:HashPepper"]);
            builder.UseSetting("Audit:IntegrityKey", values["Audit:IntegrityKey"]);
            builder.UseSetting("Backup:Directory", values["Backup:Directory"]);
            builder.UseSetting("Backup:EncryptionKeyBase64", values["Backup:EncryptionKeyBase64"]);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing || !Directory.Exists(_root)) return;
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                // Production test artifacts are isolated under the OS temp directory.
            }
            catch (UnauthorizedAccessException)
            {
                // A transient file handle must not mask the test result.
            }
        }
    }
}
