using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;

namespace WeChatBot.Backend.Services;

public sealed class BackupOptions
{
    public string Directory { get; set; } = "data/backups";
    public string EncryptionKeyBase64 { get; set; } = string.Empty;
}

public sealed record BackupVerification(Guid BackupId, bool IsValid, string ExpectedSha256, string ActualSha256, long Bytes);

internal sealed record BackupCreateCheckpoint(Guid BackupId);

/// <summary>
/// 表示逻辑备份允许保存的 Agent 注册元数据；有意不定义凭据摘要和凭据生命周期字段。
/// </summary>
internal sealed class AgentRegistrationBackup
{
    /// <summary>获取或设置 AgentRegistration 主键。</summary>
    public Guid Id { get; set; }

    /// <summary>获取或设置注册所属租户。</summary>
    public Guid TenantId { get; set; }

    /// <summary>获取或设置展示用 AgentId。</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>获取或设置规范化 AgentId。</summary>
    public string NormalizedAgentId { get; set; } = string.Empty;

    /// <summary>获取或设置固定微信实例绑定。</summary>
    public string WeChatInstanceId { get; set; } = string.Empty;

    /// <summary>获取或设置注册是否启用。</summary>
    public bool IsEnabled { get; set; }

    /// <summary>获取或设置配置版本。</summary>
    public string ConfigurationVersion { get; set; } = "1";

    /// <summary>获取或设置首次注册时间。</summary>
    public DateTimeOffset RegisteredAt { get; set; }

    /// <summary>获取或设置备份时的注册更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>获取或设置备份时的并发版本。</summary>
    public long Version { get; set; } = 1;
}

public sealed record RestoreResult(
    Guid RestoreId,
    Guid BackupId,
    Guid PreRestoreBackupId,
    IReadOnlyDictionary<string, int> Restored,
    string Mode,
    bool IsolatedEnvironmentCreated,
    bool AutomationPaused,
    bool Replayed);

internal sealed class LogicalBackupPayload
{
    /// <summary>首次包含备注任务目标身份快照并要求剥离活动租约的模式版本。</summary>
    public const int RemarkTaskLeaseSchemaVersion = 4;

    /// <summary>首次使用无凭据 Agent 注册 DTO 的模式版本。</summary>
    public const int CredentiallessAgentSchemaVersion = 5;

    /// <summary>
    /// 当前逻辑备份模式版本；版本 5 在版本 4 租约规则基础上使用无凭据 Agent DTO，禁止摘要进入载荷。
    /// </summary>
    public const int CurrentSchemaVersion = CredentiallessAgentSchemaVersion;

    /// <summary>仍可验证和恢复的最早逻辑备份版本。</summary>
    public const int MinimumSupportedSchemaVersion = 1;

    /// <summary>获取或设置备份模式版本。</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid BackupId { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
    public TenantState? Tenant { get; set; }
    public List<ServicePackage> ServicePackages { get; set; } = [];
    public List<AgentRegistrationBackup> AgentRegistrations { get; set; } = [];
    public List<Contact> Contacts { get; set; } = [];
    public List<GroupChat> Groups { get; set; } = [];
    public List<RemarkRule> RemarkRules { get; set; } = [];
    public List<RemarkTask> RemarkTasks { get; set; } = [];
    public List<GroupMentionEvent> GroupMentions { get; set; } = [];
    public List<Entitlement> Entitlements { get; set; } = [];
    public List<EntitlementLedger> EntitlementLedger { get; set; } = [];
    public List<ActivationCode> ActivationCodes { get; set; } = [];
    public List<AuditLog> AuditLogs { get; set; } = [];
}

public sealed class LogicalBackupService(
    AppDbContext db,
    TenantContext tenant,
    TimeProvider timeProvider,
    IOptions<BackupOptions> options,
    AuditService audit)
{
    private static readonly byte[] Magic = "WXB1"u8.ToArray();
    private const string CreateOperation = "backup.create";
    private static readonly SemaphoreSlim CreateGate = new(1, 1);
    private static readonly SemaphoreSlim RestoreGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<BackupManifest> CreateAsync(string reason, CancellationToken cancellationToken)
    {
        await CreateGate.WaitAsync(cancellationToken);
        try
        {
            return await CreateCoreAsync(reason, null, null, cancellationToken);
        }
        finally
        {
            CreateGate.Release();
        }
    }

    public async Task<BackupManifest> CreateIdempotentAsync(
        string reason,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        reason = reason.Trim();
        idempotencyKey = idempotencyKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            throw DomainException.Validation(
                "invalid_idempotency_key",
                "Idempotency-Key is required and must be at most 128 characters.");
        }

        var requestHash = StableHash.Sha256(reason);
        await CreateGate.WaitAsync(cancellationToken);
        try
        {
            var replay = await TryReadIdempotentCreateAsync(
                idempotencyKey,
                requestHash,
                cancellationToken);
            if (replay is not null) return replay;

            try
            {
                return await CreateCoreAsync(
                    reason,
                    idempotencyKey,
                    requestHash,
                    cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                replay = await TryReadIdempotentCreateAsync(
                    idempotencyKey,
                    requestHash,
                    cancellationToken);
                if (replay is not null) return replay;
                throw;
            }
        }
        finally
        {
            CreateGate.Release();
        }
    }

    private async Task<BackupManifest> CreateCoreAsync(
        string reason,
        string? idempotencyKey,
        string? requestHash,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var backupId = Guid.NewGuid();
        LogicalBackupPayload payload;
        await using (var snapshot = await db.Database.BeginTransactionAsync(
                         IsolationLevel.Serializable,
                         cancellationToken))
        {
            payload = new LogicalBackupPayload
            {
                BackupId = backupId,
                TenantId = tenant.TenantId,
                ExportedAt = now,
                Tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(cancellationToken),
                ServicePackages = await db.ServicePackages.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken),
                // 逻辑备份只保存注册元数据，凭据摘要和生命周期从不进入序列化对象图。
                AgentRegistrations = (await db.AgentRegistrations.AsNoTracking()
                        .OrderBy(x => x.Id)
                        .ToListAsync(cancellationToken))
                    .Select(ToAgentRegistrationBackup)
                    .ToList(),
                Contacts = await db.Contacts.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken),
                Groups = await db.Groups.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken),
                RemarkRules = await db.RemarkRules.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken),
                // 活动租约是短期授权而非业务事实；备份中只保留任务和尝试历史，避免恢复后旧令牌继续生效。
                RemarkTasks = (await db.RemarkTasks.AsNoTracking()
                        .OrderBy(x => x.Id)
                        .ToListAsync(cancellationToken))
                    .Select(ClearActiveRemarkTaskLease)
                    .ToList(),
                GroupMentions = await db.GroupMentions.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken),
                Entitlements = await db.Entitlements.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken),
                EntitlementLedger = await db.EntitlementLedger.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken),
                ActivationCodes = await db.ActivationCodes.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken),
                AuditLogs = await db.AuditLogs.AsNoTracking().OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(cancellationToken)
            };
            await snapshot.CommitAsync(cancellationToken);
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var encrypted = Encrypt(plaintext, GetEncryptionKey());
        var fileName = $"{tenant.TenantId:N}-{now:yyyyMMddHHmmss}-{backupId:N}.wxbak";
        var path = ResolvePath(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deletePayloadOnFailure = true;
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await stream.WriteAsync(encrypted, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            var counts = CalculateCounts(payload);
            var manifest = new BackupManifest
            {
                Id = backupId,
                TenantId = tenant.TenantId,
                CreatedAt = now,
                CreatedBy = tenant.Actor,
                FileName = fileName,
                PayloadSha256 = StableHash.Sha256(encrypted),
                Bytes = encrypted.LongLength,
                SchemaVersion = payload.SchemaVersion,
                CountsJson = JsonSerializer.Serialize(counts, JsonOptions),
                Status = BackupStatus.Created
            };
            db.BackupManifests.Add(manifest);
            audit.Add("backup.created", nameof(BackupManifest), manifest.Id.ToString("D"), details: new
            {
                manifest.SchemaVersion,
                manifest.Bytes,
                reason,
                counts
            });
            if (idempotencyKey is not null && requestHash is not null)
            {
                db.IdempotencyRecords.Add(new IdempotencyRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.TenantId,
                    Operation = CreateOperation,
                    Key = idempotencyKey,
                    RequestHash = requestHash,
                    StatusCode = StatusCodes.Status201Created,
                    ResponseJson = JsonSerializer.Serialize(new BackupCreateCheckpoint(manifest.Id), JsonOptions),
                    CreatedAt = now,
                    ExpiresAt = now.AddDays(7)
                });
            }
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                db.ChangeTracker.Clear();
                BackupManifest? committed;
                try
                {
                    committed = await TryReadCommittedCreateAsync(
                        backupId,
                        idempotencyKey,
                        requestHash,
                        CancellationToken.None);
                }
                catch
                {
                    // Preserve the payload when the database cannot prove whether the commit succeeded.
                    deletePayloadOnFailure = false;
                    throw;
                }
                if (committed is not null) return committed;
                throw;
            }
            return manifest;
        }
        catch
        {
            if (deletePayloadOnFailure)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // A failed create remains failed; orphan cleanup can be retried operationally.
                }
                catch (UnauthorizedAccessException)
                {
                    // Preserve the original creation error.
                }
            }
            throw;
        }
    }

    private async Task<BackupManifest?> TryReadIdempotentCreateAsync(
        string idempotencyKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var record = await db.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Operation == CreateOperation && x.Key == idempotencyKey,
                cancellationToken);
        if (record is null) return null;
        if (!HashEquals(record.RequestHash, requestHash))
        {
            throw DomainException.Conflict(
                "idempotency_key_reused",
                "The Idempotency-Key was already used for a different backup request.");
        }

        BackupCreateCheckpoint checkpoint;
        try
        {
            checkpoint = JsonSerializer.Deserialize<BackupCreateCheckpoint>(record.ResponseJson, JsonOptions)
                         ?? throw new JsonException("Backup checkpoint is empty.");
        }
        catch (JsonException)
        {
            throw DomainException.Conflict(
                "backup_idempotency_inconsistent",
                "The backup idempotency checkpoint is invalid and requires administrator review.");
        }

        return await db.BackupManifests.AsNoTracking()
                   .SingleOrDefaultAsync(x => x.Id == checkpoint.BackupId, cancellationToken)
               ?? throw DomainException.Conflict(
                   "backup_idempotency_inconsistent",
                   "The backup idempotency checkpoint does not reference an available manifest.");
    }

    private async Task<BackupManifest?> TryReadCommittedCreateAsync(
        Guid backupId,
        string? idempotencyKey,
        string? requestHash,
        CancellationToken cancellationToken)
    {
        var manifest = await db.BackupManifests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == backupId, cancellationToken);
        if (manifest is null) return null;

        if (idempotencyKey is not null && requestHash is not null)
        {
            var replay = await TryReadIdempotentCreateAsync(
                idempotencyKey,
                requestHash,
                cancellationToken);
            if (replay is null || replay.Id != backupId)
            {
                throw DomainException.Conflict(
                    "backup_idempotency_inconsistent",
                    "The committed backup does not match its idempotency checkpoint.");
            }
        }

        var path = ResolvePath(manifest.FileName);
        if (!File.Exists(path))
        {
            throw DomainException.Conflict(
                "backup_payload_missing",
                "The committed backup manifest has no payload file.");
        }
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.LongLength != manifest.Bytes ||
            !HashEquals(manifest.PayloadSha256, StableHash.Sha256(bytes)))
        {
            throw DomainException.Conflict(
                "backup_payload_inconsistent",
                "The committed backup payload does not match its manifest.");
        }
        return manifest;
    }

    public async Task<BackupVerification> VerifyAsync(Guid backupId, CancellationToken cancellationToken)
    {
        var manifest = await db.BackupManifests.SingleOrDefaultAsync(x => x.Id == backupId, cancellationToken)
                       ?? throw DomainException.NotFound("Backup manifest");
        var bytes = await ReadBackupAsync(manifest, cancellationToken);
        var actual = StableHash.Sha256(bytes);
        var valid = HashEquals(manifest.PayloadSha256, actual);
        if (valid)
        {
            try
            {
                var plaintext = Decrypt(bytes, GetEncryptionKey());
                var payload = JsonSerializer.Deserialize<LogicalBackupPayload>(plaintext, JsonOptions)
                              ?? throw new JsonException("Backup payload is empty.");
                var expectedCounts = JsonSerializer.Deserialize<Dictionary<string, int>>(manifest.CountsJson, JsonOptions)
                                     ?? throw new JsonException("Backup manifest counts are empty.");
                await ValidatePayloadAsync(payload, manifest, expectedCounts, cancellationToken);
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException or DomainException)
            {
                valid = false;
            }
        }

        manifest.Status = valid ? BackupStatus.Verified : BackupStatus.Corrupt;
        manifest.VerifiedAt = timeProvider.GetUtcNow();
        audit.Add("backup.verified", nameof(BackupManifest), manifest.Id.ToString("D"), valid, new { valid, actual });
        await db.SaveChangesAsync(cancellationToken);
        return new BackupVerification(manifest.Id, valid, manifest.PayloadSha256, actual, bytes.LongLength);
    }

    public async Task<RestoreResult> RestoreAsync(
        Guid backupId,
        string confirmation,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await RestoreGate.WaitAsync(cancellationToken);
        try
        {
            return await RestoreCoreAsync(backupId, confirmation, idempotencyKey, cancellationToken);
        }
        finally
        {
            RestoreGate.Release();
        }
    }

    private async Task<RestoreResult> RestoreCoreAsync(
        Guid backupId,
        string confirmation,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        idempotencyKey = idempotencyKey?.Trim() ?? string.Empty;
        if (!string.Equals(confirmation, "RESTORE", StringComparison.Ordinal))
            throw DomainException.Validation("restore_confirmation_required", "Set confirmation to RESTORE to perform a logical restore.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
            throw DomainException.Validation("invalid_idempotency_key", "Idempotency-Key is required and must be at most 128 characters.");

        var existingRestore = await db.RestoreOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existingRestore is not null)
        {
            if (existingRestore.BackupManifestId != backupId)
                throw DomainException.Conflict("idempotency_key_reused", "The Idempotency-Key was already used for another backup.");
            var stored = JsonSerializer.Deserialize<RestoreResult>(existingRestore.ReportJson, JsonOptions)
                         ?? throw new InvalidOperationException("Stored restore report is invalid.");
            return stored with { Replayed = true };
        }

        var manifest = await db.BackupManifests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == backupId, cancellationToken)
                       ?? throw DomainException.NotFound("Backup manifest");
        var bytes = await ReadBackupAsync(manifest, cancellationToken);
        var actualHash = StableHash.Sha256(bytes);
        if (!HashEquals(manifest.PayloadSha256, actualHash))
            throw DomainException.Conflict("backup_checksum_failed", "Backup checksum verification failed; restore was not started.");

        LogicalBackupPayload payload;
        Dictionary<string, int> manifestCounts;
        try
        {
            payload = JsonSerializer.Deserialize<LogicalBackupPayload>(Decrypt(bytes, GetEncryptionKey()), JsonOptions)
                      ?? throw new JsonException("Backup payload is empty.");
            manifestCounts = JsonSerializer.Deserialize<Dictionary<string, int>>(manifest.CountsJson, JsonOptions)
                             ?? throw new JsonException("Backup manifest counts are empty.");
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            throw DomainException.Conflict("backup_payload_invalid", "Backup decryption or payload validation failed; restore was not started.");
        }
        await ValidatePayloadAsync(payload, manifest, manifestCounts, cancellationToken);

        var startedAt = timeProvider.GetUtcNow();
        var tenantState = await db.Tenants.SingleAsync(cancellationToken);
        if (!tenantState.AutomationPaused)
        {
            tenantState.AutomationPaused = true;
            tenantState.UpdatedAt = startedAt;
            tenantState.Version++;
            audit.Add(
                "automation.paused-for-restore",
                nameof(TenantState),
                tenantState.TenantId.ToString("D"),
                details: new { backupId });
            await db.SaveChangesAsync(cancellationToken);
        }

        var preRestore = await CreateAsync("automatic-pre-restore", cancellationToken);
        db.ChangeTracker.Clear();
        var restored = new Dictionary<string, int>(StringComparer.Ordinal);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        tenantState = await db.Tenants.SingleAsync(cancellationToken);
        if (payload.Tenant is not null)
        {
            tenantState.Name = payload.Tenant.Name;
        }
        tenantState.AutomationPaused = true;
        tenantState.UpdatedAt = startedAt;
        tenantState.Version++;

        restored["servicePackages"] = await RestoreServicePackagesAsync(payload.ServicePackages, cancellationToken);
        // 现存注册不被旧备份覆盖，但其当前凭据同样属于恢复前授权，必须全部失效并重新签发。
        await RevokeExistingAgentCredentialsForRestoreAsync(startedAt, cancellationToken);
        restored["agentRegistrations"] = await RestoreAgentRegistrationsAsync(payload.AgentRegistrations, cancellationToken);
        restored["contacts"] = await RestoreContactsAsync(payload.Contacts, cancellationToken);
        restored["groups"] = await RestoreGroupsAsync(payload.Groups, cancellationToken);
        restored["remarkRules"] = await RestoreRemarkRulesAsync(payload.RemarkRules, cancellationToken);
        // schema v1-v3 尚未保存任务身份快照；目标合并完成后再从备份目标或当前数据库补齐空字段。
        await PopulateLegacyRemarkTaskIdentitySnapshotsAsync(payload, cancellationToken);
        // 兼容旧备份时也强制剥离租约字段；任何恢复出的待处理任务都必须重新认领。
        var restorableRemarkTasks = payload.RemarkTasks
            .Select(ClearActiveRemarkTaskLease)
            .ToList();
        restored["remarkTasks"] = await AddMissingAsync(
            db.RemarkTasks,
            restorableRemarkTasks,
            x => x.Id,
            cancellationToken);
        restored["entitlements"] = await AddMissingAsync(db.Entitlements, payload.Entitlements, x => x.Id, cancellationToken);
        restored["activationCodes"] = await AddMissingAsync(db.ActivationCodes, payload.ActivationCodes, x => x.Id, cancellationToken);
        restored["entitlementLedger"] = await AddMissingAsync(db.EntitlementLedger, payload.EntitlementLedger, x => x.Id, cancellationToken);
        restored["groupMentions"] = await AddMissingAsync(db.GroupMentions, payload.GroupMentions, x => x.Id, cancellationToken);
        restored["auditLogs"] = await AddMissingAuditLogsAsync(payload.AuditLogs, cancellationToken);

        // Existing entitlement, redemption and ledger rows are authoritative and are never overwritten by an older snapshot.
        var restoreId = Guid.NewGuid();
        var completedAt = timeProvider.GetUtcNow();
        var result = new RestoreResult(
            restoreId,
            backupId,
            preRestore.Id,
            restored,
            "in-place-merge",
            false,
            true,
            false);
        var reportJson = JsonSerializer.Serialize(result, JsonOptions);
        db.RestoreOperations.Add(new RestoreOperation
        {
            Id = restoreId,
            TenantId = tenant.TenantId,
            BackupManifestId = backupId,
            PreRestoreBackupManifestId = preRestore.Id,
            IdempotencyKey = idempotencyKey,
            Actor = tenant.Actor,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Status = "completed",
            ReportJson = reportJson
        });
        audit.Add("backup.restored", nameof(BackupManifest), backupId.ToString("D"), details: new
        {
            restoreId,
            preRestoreBackupId = preRestore.Id,
            restored,
            automationPaused = true,
            mergeOnly = true,
            authoritativeFactsPreserved = true
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<int> RestoreContactsAsync(List<Contact> source, CancellationToken cancellationToken)
    {
        var existing = await db.Contacts.ToDictionaryAsync(x => x.Id, cancellationToken);
        var count = 0;
        foreach (var item in source.Where(x => x.TenantId == tenant.TenantId))
        {
            if (existing.TryGetValue(item.Id, out var target))
            {
                target.ExternalId = item.ExternalId;
                target.DisplayName = item.DisplayName;
                target.WeChatId = item.WeChatId;
                target.CustomerCode = item.CustomerCode;
                target.SystemRemark = item.SystemRemark;
                target.CurrentWeChatRemark = item.CurrentWeChatRemark;
                target.ManualRemarkProtected = item.ManualRemarkProtected;
                target.ServiceExpiresAt = item.ServiceExpiresAt;
                target.UpdatedAt = timeProvider.GetUtcNow();
                target.Version++;
            }
            else
            {
                db.Contacts.Add(item);
            }
            count++;
        }
        return count;
    }

    private async Task<int> RestoreAgentRegistrationsAsync(
        List<AgentRegistrationBackup> source,
        CancellationToken cancellationToken)
    {
        var existing = await db.AgentRegistrations.AsNoTracking().ToListAsync(cancellationToken);
        var existingIds = existing.Select(x => x.Id).ToHashSet();
        var existingAgentIds = existing.Select(x => x.NormalizedAgentId).ToHashSet(StringComparer.Ordinal);
        var existingInstanceIds = existing.Select(x => x.WeChatInstanceId).ToHashSet(StringComparer.Ordinal);
        var restored = 0;

        foreach (var item in source.Where(x => x.TenantId == tenant.TenantId))
        {
            var normalizedAgentId = AgentControlService.NormalizeAgentId(item.AgentId);
            if (string.IsNullOrWhiteSpace(normalizedAgentId) ||
                string.IsNullOrWhiteSpace(item.WeChatInstanceId) ||
                existingIds.Contains(item.Id) ||
                existingAgentIds.Contains(normalizedAgentId) ||
                existingInstanceIds.Contains(item.WeChatInstanceId.Trim()))
            {
                continue;
            }

            var agentId = item.AgentId.Trim();
            var instanceId = item.WeChatInstanceId.Trim();
            var configurationVersion = string.IsNullOrWhiteSpace(item.ConfigurationVersion)
                ? "1"
                : item.ConfigurationVersion.Trim();
            // 无论载荷 schema 新旧，恢复出的注册都不继承灾备前认证能力，管理员必须重新轮换签发。
            db.AgentRegistrations.Add(new AgentRegistration
            {
                Id = item.Id,
                TenantId = item.TenantId,
                AgentId = agentId,
                NormalizedAgentId = normalizedAgentId,
                WeChatInstanceId = instanceId,
                IsEnabled = item.IsEnabled,
                ConfigurationVersion = configurationVersion,
                CredentialHash = null,
                CredentialIssuedAt = null,
                CredentialRotatedAt = null,
                CredentialRevokedAt = timeProvider.GetUtcNow(),
                RegisteredAt = item.RegisteredAt,
                UpdatedAt = timeProvider.GetUtcNow(),
                Version = Math.Max(1, item.Version + 1)
            });
            existingIds.Add(item.Id);
            existingAgentIds.Add(normalizedAgentId);
            existingInstanceIds.Add(instanceId);
            restored++;
        }

        return restored;
    }

    /// <summary>
    /// 在恢复事务内清除当前租户所有现存 Agent 凭据，确保恢复完成后灾备前任何明文都不能继续认证。
    /// </summary>
    /// <param name="revokedAt">本次恢复开始的统一吊销时间。</param>
    /// <param name="cancellationToken">数据库查询取消令牌。</param>
    private async Task RevokeExistingAgentCredentialsForRestoreAsync(
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        var registrations = await db.AgentRegistrations
            .Where(x => x.CredentialHash != null || x.CredentialRevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var registration in registrations)
        {
            // 摘要本身也是认证材料，恢复时必须物理清除；保留吊销时间供管理员判断需重新签发。
            registration.CredentialHash = null;
            registration.CredentialRevokedAt = revokedAt;
            registration.UpdatedAt = revokedAt;
            registration.Version++;
        }

        // 心跳状态作为运维遥测保留。凭据被清除后列表与租约门禁立即离线；后续轮换时间会建立
        // 新的会话边界，因此重新签发后的 Agent 仍必须发送新心跳才能领取租约。
    }

    /// <summary>将数据库注册投影为明确不含凭据字段的备份 DTO。</summary>
    /// <param name="source">当前数据库中的 AgentRegistration。</param>
    /// <returns>只包含可恢复注册元数据的独立对象。</returns>
    private static AgentRegistrationBackup ToAgentRegistrationBackup(AgentRegistration source) => new()
    {
        Id = source.Id,
        TenantId = source.TenantId,
        AgentId = source.AgentId,
        NormalizedAgentId = source.NormalizedAgentId,
        WeChatInstanceId = source.WeChatInstanceId,
        IsEnabled = source.IsEnabled,
        ConfigurationVersion = source.ConfigurationVersion,
        RegisteredAt = source.RegisteredAt,
        UpdatedAt = source.UpdatedAt,
        Version = source.Version
    };

    private async Task<int> RestoreServicePackagesAsync(
        IReadOnlyCollection<ServicePackage> source,
        CancellationToken cancellationToken)
    {
        var existing = await db.ServicePackages.ToDictionaryAsync(x => x.Id, cancellationToken);
        var restored = 0;
        foreach (var item in source)
        {
            if (existing.TryGetValue(item.Id, out var target))
            {
                target.Code = item.Code;
                target.Name = item.Name;
                target.Tier = item.Tier;
                target.FeaturesJson = item.FeaturesJson;
                target.IsEnabled = item.IsEnabled;
                target.Version++;
            }
            else
            {
                db.ServicePackages.Add(item);
            }
            restored++;
        }
        return restored;
    }

    private async Task<int> RestoreGroupsAsync(List<GroupChat> source, CancellationToken cancellationToken)
    {
        var existing = await db.Groups.ToDictionaryAsync(x => x.Id, cancellationToken);
        var count = 0;
        foreach (var item in source.Where(x => x.TenantId == tenant.TenantId))
        {
            if (existing.TryGetValue(item.Id, out var target))
            {
                target.ExternalId = item.ExternalId;
                target.DisplayName = item.DisplayName;
                target.BusinessName = item.BusinessName;
                target.SystemRemark = item.SystemRemark;
                target.CurrentWeChatRemark = item.CurrentWeChatRemark;
                target.ManualRemarkProtected = item.ManualRemarkProtected;
                target.ServiceExpiresAt = item.ServiceExpiresAt;
                target.UpdatedAt = timeProvider.GetUtcNow();
                target.Version++;
            }
            else
            {
                db.Groups.Add(item);
            }
            count++;
        }
        return count;
    }

    private async Task<int> RestoreRemarkRulesAsync(List<RemarkRule> source, CancellationToken cancellationToken)
    {
        var existing = await db.RemarkRules.ToDictionaryAsync(x => x.Id, cancellationToken);
        var count = 0;
        foreach (var item in source.Where(x => x.TenantId == tenant.TenantId))
        {
            if (existing.TryGetValue(item.Id, out var target))
            {
                target.Name = item.Name;
                target.TargetKind = item.TargetKind;
                target.Template = item.Template;
                target.ConflictPolicy = item.ConflictPolicy;
                target.IsEnabled = item.IsEnabled;
                target.MaxLength = item.MaxLength;
                target.UpdatedAt = timeProvider.GetUtcNow();
                target.Version++;
            }
            else
            {
                db.RemarkRules.Add(item);
            }
            count++;
        }
        return count;
    }

    private async Task<int> AddMissingAsync<TEntity>(
        DbSet<TEntity> set,
        IEnumerable<TEntity> source,
        Func<TEntity, Guid> keySelector,
        CancellationToken cancellationToken) where TEntity : class, ITenantEntity
    {
        var currentIds = await set.AsNoTracking().Select(x => EF.Property<Guid>(x, "Id")).ToHashSetAsync(cancellationToken);
        var missing = source
            .Where(x => x.TenantId == tenant.TenantId && !currentIds.Contains(keySelector(x)))
            .ToList();
        if (missing.Count > 0) await set.AddRangeAsync(missing, cancellationToken);
        return missing.Count;
    }

    /// <summary>
    /// 为 schema v1-v3 备份中缺失的备注任务身份快照补值；schema v4 必须继续通过其原有严格载荷校验。
    /// </summary>
    /// <param name="payload">已经通过租户、计数和引用完整性预校验的逻辑备份载荷。</param>
    /// <param name="cancellationToken">用于取消数据库目标查询的令牌。</param>
    private async Task PopulateLegacyRemarkTaskIdentitySnapshotsAsync(
        LogicalBackupPayload payload,
        CancellationToken cancellationToken)
    {
        // v4 起在进入恢复事务前已要求两项快照均非空，禁止用恢复时目标值掩盖损坏或篡改载荷。
        if (payload.SchemaVersion >= LogicalBackupPayload.RemarkTaskLeaseSchemaVersion) return;

        // 只处理至少缺失一项身份字段的旧任务，已有非空字段必须保持备份中的原始值。
        var incompleteTasks = payload.RemarkTasks
            .Where(task =>
                string.IsNullOrWhiteSpace(task.TargetExternalId) ||
                string.IsNullOrWhiteSpace(task.ExpectedTargetDisplayName))
            .ToArray();
        if (incompleteTasks.Length == 0) return;

        // 备份集合优先：同一恢复事务刚按这些值合并目标，任务快照应与灾备时目标身份保持一致。
        var backupContacts = payload.Contacts.ToDictionary(contact => contact.Id);
        var backupGroups = payload.Groups.ToDictionary(group => group.Id);
        // 当前数据库只为备份未携带的目标提供回退，查询范围显式限定当前租户和实际缺失的目标 ID。
        var fallbackContactIds = incompleteTasks
            .Where(task =>
                task.TargetKind == ServiceTargetKind.Contact &&
                !backupContacts.ContainsKey(task.TargetId))
            .Select(task => task.TargetId)
            .Distinct()
            .ToArray();
        var fallbackGroupIds = incompleteTasks
            .Where(task =>
                task.TargetKind == ServiceTargetKind.Group &&
                !backupGroups.ContainsKey(task.TargetId))
            .Select(task => task.TargetId)
            .Distinct()
            .ToArray();
        var currentContacts = await db.Contacts
            .Where(contact => fallbackContactIds.Contains(contact.Id))
            .ToDictionaryAsync(contact => contact.Id, cancellationToken);
        var currentGroups = await db.Groups
            .Where(group => fallbackGroupIds.Contains(group.Id))
            .ToDictionaryAsync(group => group.Id, cancellationToken);

        foreach (var task in incompleteTasks)
        {
            // 目标解析同时匹配任务声明的类型和主键；备份载荷及 DbContext 查询过滤器已经限定当前租户。
            var identity = task.TargetKind switch
            {
                ServiceTargetKind.Contact when backupContacts.TryGetValue(task.TargetId, out var backupContact) =>
                    new RemarkTargetIdentity(backupContact.ExternalId, backupContact.DisplayName),
                ServiceTargetKind.Contact when currentContacts.TryGetValue(task.TargetId, out var currentContact) =>
                    new RemarkTargetIdentity(currentContact.ExternalId, currentContact.DisplayName),
                ServiceTargetKind.Group when backupGroups.TryGetValue(task.TargetId, out var backupGroup) =>
                    new RemarkTargetIdentity(backupGroup.ExternalId, backupGroup.DisplayName),
                ServiceTargetKind.Group when currentGroups.TryGetValue(task.TargetId, out var currentGroup) =>
                    new RemarkTargetIdentity(currentGroup.ExternalId, currentGroup.DisplayName),
                _ => null
            };
            if (identity is null)
            {
                // 不制造永久不可领取的任务；该异常会回滚恢复事务，现有引用错误码保持 API 兼容。
                throw DomainException.Conflict(
                    "backup_reference_integrity_failed",
                    "A legacy remark task target is unavailable in both the backup and the current tenant database.");
            }
            if (string.IsNullOrWhiteSpace(identity.ExternalId) ||
                string.IsNullOrWhiteSpace(identity.DisplayName))
            {
                // 已找到但没有完整稳定身份的目标同样不能用于生成可执行任务快照。
                throw DomainException.Conflict(
                    "backup_remark_task_identity_invalid",
                    "A legacy remark task target does not provide a complete stable identity snapshot.");
            }

            // 分字段补齐：若旧载荷已携带其中一项非空快照，必须保留该历史事实而不是整体覆盖。
            if (string.IsNullOrWhiteSpace(task.TargetExternalId))
            {
                task.TargetExternalId = identity.ExternalId;
            }
            if (string.IsNullOrWhiteSpace(task.ExpectedTargetDisplayName))
            {
                task.ExpectedTargetDisplayName = identity.DisplayName;
            }
        }
    }

    /// <summary>表示从联系人或群解析出的稳定外部标识和显示名称。</summary>
    /// <param name="ExternalId">目标在微信侧或上游数据源中的稳定外部标识。</param>
    /// <param name="DisplayName">恢复时用于执行前身份核验的目标显示名称。</param>
    private sealed record RemarkTargetIdentity(string ExternalId, string DisplayName);

    /// <summary>
    /// 清除备注任务上的短期租约授权，同时保留任务状态、尝试次数和已完成结果标识。
    /// </summary>
    /// <param name="source">来自数据库或备份载荷的任务。</param>
    /// <returns>不含活动租约持有证明的独立任务副本。</returns>
    private static RemarkTask ClearActiveRemarkTaskLease(RemarkTask source) => new()
    {
        Id = source.Id,
        TenantId = source.TenantId,
        RuleId = source.RuleId,
        TargetKind = source.TargetKind,
        TargetId = source.TargetId,
        TargetExternalId = source.TargetExternalId,
        ExpectedTargetDisplayName = source.ExpectedTargetDisplayName,
        IdempotencyKey = source.IdempotencyKey,
        RequestHash = source.RequestHash,
        GeneratedRemark = source.GeneratedRemark,
        OriginalSystemRemark = source.OriginalSystemRemark,
        OriginalWeChatRemark = source.OriginalWeChatRemark,
        Status = source.Status,
        ConflictReason = source.ConflictReason,
        FailureReason = source.FailureReason,
        ClaimedByAgentId = null,
        ClaimedWeChatInstanceId = null,
        LeaseTokenHash = null,
        LeaseExpiresAt = null,
        AttemptCount = source.AttemptCount,
        CompletionResultId = source.CompletionResultId,
        CreatedAt = source.CreatedAt,
        CompletedAt = source.CompletedAt,
        Version = source.Version
    };

    private void EnsureAuditLogsHaveValidIntegrity(IEnumerable<AuditLog> source)
    {
        if (source.Any(x => x.TenantId != tenant.TenantId || !audit.HasValidIntegrity(x)))
        {
            throw DomainException.Conflict(
                "backup_audit_integrity_failed",
                "Backup audit records failed tenant or integrity validation; restore was not started.");
        }
    }

    private async Task ValidatePayloadAsync(
        LogicalBackupPayload payload,
        BackupManifest manifest,
        IReadOnlyDictionary<string, int> manifestCounts,
        CancellationToken cancellationToken)
    {
        EnsurePayloadCollectionsArePresent(payload);
        if (!IsSupportedSchema(payload.SchemaVersion) ||
            payload.SchemaVersion != manifest.SchemaVersion ||
            payload.SchemaVersion >= 3 && payload.BackupId != manifest.Id ||
            payload.TenantId != tenant.TenantId)
        {
            throw DomainException.Conflict(
                "backup_schema_mismatch",
                "Backup schema or tenant does not match the restore target.");
        }
        if (!CountsEqual(manifestCounts, CalculateCounts(payload)))
        {
            throw DomainException.Conflict(
                "backup_manifest_mismatch",
                "Backup record counts do not match the manifest.");
        }

        EnsurePayloadTenantIsolation(payload);
        if (payload.SchemaVersion >= LogicalBackupPayload.RemarkTaskLeaseSchemaVersion &&
            payload.RemarkTasks.Any(x =>
                string.IsNullOrWhiteSpace(x.TargetExternalId) ||
                string.IsNullOrWhiteSpace(x.ExpectedTargetDisplayName)))
        {
            throw DomainException.Conflict(
                "backup_remark_task_identity_invalid",
                "Version 4 or newer remark tasks must include stable target identity snapshots; restore was not started.");
        }
        await EnsurePayloadReferentialIntegrityAsync(payload, cancellationToken);
        await EnsurePayloadPackageReferencesAsync(payload, cancellationToken);
        EnsureAuditLogsHaveValidIntegrity(payload.AuditLogs);
    }

    private static void EnsurePayloadCollectionsArePresent(LogicalBackupPayload payload)
    {
        if (payload.AgentRegistrations is null ||
            payload.ServicePackages is null ||
            payload.Contacts is null ||
            payload.Groups is null ||
            payload.RemarkRules is null ||
            payload.RemarkTasks is null ||
            payload.GroupMentions is null ||
            payload.Entitlements is null ||
            payload.EntitlementLedger is null ||
            payload.ActivationCodes is null ||
            payload.AuditLogs is null)
        {
            throw DomainException.Conflict(
                "backup_payload_invalid",
                "Backup payload is missing one or more required collections.");
        }
    }

    private void EnsurePayloadTenantIsolation(LogicalBackupPayload payload)
    {
        var tenantId = tenant.TenantId;
        var containsForeignTenant =
            (payload.Tenant is not null && payload.Tenant.TenantId != tenantId) ||
            payload.AgentRegistrations.Any(x => x.TenantId != tenantId) ||
            payload.Contacts.Any(x => x.TenantId != tenantId) ||
            payload.Groups.Any(x => x.TenantId != tenantId) ||
            payload.RemarkRules.Any(x => x.TenantId != tenantId) ||
            payload.RemarkTasks.Any(x => x.TenantId != tenantId) ||
            payload.GroupMentions.Any(x => x.TenantId != tenantId) ||
            payload.Entitlements.Any(x => x.TenantId != tenantId) ||
            payload.EntitlementLedger.Any(x => x.TenantId != tenantId) ||
            payload.ActivationCodes.Any(x => x.TenantId != tenantId) ||
            payload.AuditLogs.Any(x => x.TenantId != tenantId);
        if (containsForeignTenant)
        {
            throw DomainException.Conflict(
                "backup_tenant_scope_invalid",
                "Backup payload contains records outside the restore tenant; restore was not started.");
        }
    }

    private async Task EnsurePayloadReferentialIntegrityAsync(
        LogicalBackupPayload payload,
        CancellationToken cancellationToken)
    {
        var contactIds = payload.Contacts.Select(x => x.Id).ToHashSet();
        var groupIds = payload.Groups.Select(x => x.Id).ToHashSet();
        var ruleIds = payload.RemarkRules.Select(x => x.Id).ToHashSet();
        var entitlementIds = payload.Entitlements.Select(x => x.Id).ToHashSet();
        var activationCodeIds = payload.ActivationCodes.Select(x => x.Id).ToHashSet();

        var referencedRuleIds = payload.RemarkTasks.Select(x => x.RuleId)
            .Where(x => !ruleIds.Contains(x))
            .Distinct()
            .ToArray();
        var validExistingRuleIds = await db.RemarkRules.AsNoTracking()
            .Where(x => referencedRuleIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToHashSetAsync(cancellationToken);
        var referencedContactIds = payload.RemarkTasks
            .Where(x => x.TargetKind == ServiceTargetKind.Contact)
            .Select(x => x.TargetId)
            .Concat(payload.Entitlements
                .Where(x => x.TargetKind == ServiceTargetKind.Contact)
                .Select(x => x.TargetId))
            .Where(x => !contactIds.Contains(x))
            .Distinct()
            .ToArray();
        var validExistingContactIds = await db.Contacts.AsNoTracking()
            .Where(x => referencedContactIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToHashSetAsync(cancellationToken);
        var allReferencedGroupIds = payload.GroupMentions.Select(x => x.GroupId)
            .Concat(payload.RemarkTasks
                .Where(x => x.TargetKind == ServiceTargetKind.Group)
                .Select(x => x.TargetId))
            .Concat(payload.Entitlements
                .Where(x => x.TargetKind == ServiceTargetKind.Group)
                .Select(x => x.TargetId));
        var referencedGroupIds = allReferencedGroupIds
            .Where(x => !groupIds.Contains(x))
            .Distinct()
            .ToArray();
        var validExistingGroupIds = await db.Groups.AsNoTracking()
            .Where(x => referencedGroupIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToHashSetAsync(cancellationToken);
        var referencedEntitlementIds = payload.EntitlementLedger.Select(x => x.EntitlementId)
            .Concat(payload.GroupMentions
                .Where(x => x.EntitlementId.HasValue)
                .Select(x => x.EntitlementId!.Value))
            .Where(x => !entitlementIds.Contains(x))
            .Distinct()
            .ToArray();
        var validExistingEntitlementIds = await db.Entitlements.AsNoTracking()
            .Where(x => referencedEntitlementIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToHashSetAsync(cancellationToken);

        var containsInvalidIds =
            HasInvalidOrDuplicateIds(payload.AgentRegistrations, x => x.Id) ||
            payload.AgentRegistrations.Any(x =>
                string.IsNullOrWhiteSpace(x.AgentId) ||
                string.IsNullOrWhiteSpace(x.WeChatInstanceId) ||
                string.IsNullOrWhiteSpace(x.ConfigurationVersion)) ||
            payload.AgentRegistrations
                .Select(x => AgentControlService.NormalizeAgentId(x.AgentId))
                .Distinct(StringComparer.Ordinal)
                .Count() != payload.AgentRegistrations.Count ||
            payload.AgentRegistrations
                .Select(x => x.WeChatInstanceId.Trim())
                .Distinct(StringComparer.Ordinal)
                .Count() != payload.AgentRegistrations.Count ||
            HasInvalidOrDuplicateIds(payload.Contacts, x => x.Id) ||
            HasInvalidOrDuplicateIds(payload.Groups, x => x.Id) ||
            HasInvalidOrDuplicateIds(payload.RemarkRules, x => x.Id) ||
            HasInvalidOrDuplicateIds(payload.RemarkTasks, x => x.Id) ||
            HasInvalidOrDuplicateIds(payload.GroupMentions, x => x.Id) ||
            HasInvalidOrDuplicateIds(payload.Entitlements, x => x.Id) ||
            HasInvalidOrDuplicateIds(payload.EntitlementLedger, x => x.Id) ||
            HasInvalidOrDuplicateIds(payload.ActivationCodes, x => x.Id) ||
            HasInvalidOrDuplicateIds(payload.AuditLogs, x => x.Id);
        var containsInvalidReferences =
            payload.RemarkTasks.Any(x =>
                !ruleIds.Contains(x.RuleId) && !validExistingRuleIds.Contains(x.RuleId) ||
                !TargetExists(
                    x.TargetKind,
                    x.TargetId,
                    contactIds,
                    validExistingContactIds,
                    groupIds,
                    validExistingGroupIds)) ||
            payload.GroupMentions.Any(x =>
                !groupIds.Contains(x.GroupId) && !validExistingGroupIds.Contains(x.GroupId) ||
                x.EntitlementId.HasValue &&
                !entitlementIds.Contains(x.EntitlementId.Value) &&
                !validExistingEntitlementIds.Contains(x.EntitlementId.Value)) ||
            payload.Entitlements.Any(x =>
                !TargetExists(
                    x.TargetKind,
                    x.TargetId,
                    contactIds,
                    validExistingContactIds,
                    groupIds,
                    validExistingGroupIds) ||
                (x.ActivationCodeId.HasValue && !activationCodeIds.Contains(x.ActivationCodeId.Value))) ||
            payload.EntitlementLedger.Any(x =>
                !entitlementIds.Contains(x.EntitlementId) &&
                !validExistingEntitlementIds.Contains(x.EntitlementId)) ||
            payload.ActivationCodes.Any(x =>
                (x.EntitlementId.HasValue && !entitlementIds.Contains(x.EntitlementId.Value)) ||
                (x.RedeemedAt.HasValue != x.EntitlementId.HasValue) ||
                (x.RedeemedAt.HasValue != x.RedeemedTargetKind.HasValue) ||
                (x.RedeemedAt.HasValue != x.RedeemedTargetId.HasValue));

        if (containsInvalidIds || containsInvalidReferences)
        {
            throw DomainException.Conflict(
                "backup_reference_integrity_failed",
                "Backup records contain invalid or cross-tenant references; restore was not started.");
        }
    }

    private async Task EnsurePayloadPackageReferencesAsync(
        LogicalBackupPayload payload,
        CancellationToken cancellationToken)
    {
        var packageIds = payload.Entitlements.Select(x => x.PackageId)
            .Concat(payload.ActivationCodes.Select(x => x.PackageId))
            .Distinct()
            .ToArray();

        if (payload.SchemaVersion >= 3 && HasInvalidServicePackageDefinitions(payload.ServicePackages))
        {
            throw DomainException.Conflict(
                "backup_package_reference_invalid",
                "Backup package definitions are invalid; restore was not started.");
        }

        var packages = payload.SchemaVersion >= 3
            ? payload.ServicePackages.ToDictionary(x => x.Id)
            : await db.ServicePackages.AsNoTracking()
                .Where(x => packageIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken);
        var invalidPackageReference = packageIds.Any(x => !packages.ContainsKey(x));
        var packageIdentityConflict = false;
        if (payload.SchemaVersion >= 3)
        {
            var packagesByCode = payload.ServicePackages.ToDictionary(
                x => x.Code,
                StringComparer.OrdinalIgnoreCase);
            var existingPackages = await db.ServicePackages.AsNoTracking()
                .Select(x => new { x.Id, x.Code })
                .ToListAsync(cancellationToken);
            packageIdentityConflict = existingPackages.Any(existing =>
                packagesByCode.TryGetValue(existing.Code, out var restored) &&
                restored.Id != existing.Id);
        }
        var invalidAdvancedTarget = payload.Entitlements.Any(x =>
                packages.TryGetValue(x.PackageId, out var package) &&
                package.Tier == PackageTier.AdvancedGeneral &&
                x.TargetKind != ServiceTargetKind.Group) ||
            payload.ActivationCodes.Any(x =>
                x.RedeemedAt.HasValue &&
                packages.TryGetValue(x.PackageId, out var package) &&
                package.Tier == PackageTier.AdvancedGeneral &&
                x.RedeemedTargetKind != ServiceTargetKind.Group);
        if (invalidPackageReference || packageIdentityConflict || invalidAdvancedTarget)
        {
            throw DomainException.Conflict(
                "backup_package_reference_invalid",
                "Backup package identities or references conflict with the restore target; restore was not started.");
        }
    }

    private static bool HasInvalidServicePackageDefinitions(IReadOnlyCollection<ServicePackage> source)
    {
        if (source.Any(x => x is null)) return true;

        return HasInvalidOrDuplicateIds(source, x => x.Id) ||
               source.Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != source.Count ||
               source.Any(x =>
                   string.IsNullOrWhiteSpace(x.Code) || x.Code.Length > 64 ||
                   string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 128 ||
                   !Enum.IsDefined(x.Tier) ||
                   !IsValidFeatureJson(x.FeaturesJson) ||
                   x.Version < 1);
    }

    private static bool IsValidFeatureJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Array &&
                   document.RootElement.EnumerateArray().All(x =>
                       x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasInvalidOrDuplicateIds<TEntity>(
        IReadOnlyCollection<TEntity> source,
        Func<TEntity, Guid> keySelector)
    {
        var ids = source.Select(keySelector).ToArray();
        return ids.Any(x => x == Guid.Empty) || ids.Distinct().Count() != ids.Length;
    }

    private static bool TargetExists(
        ServiceTargetKind targetKind,
        Guid targetId,
        IReadOnlySet<Guid> payloadContactIds,
        IReadOnlySet<Guid> existingContactIds,
        IReadOnlySet<Guid> payloadGroupIds,
        IReadOnlySet<Guid> existingGroupIds) => targetKind switch
        {
            ServiceTargetKind.Contact => payloadContactIds.Contains(targetId) || existingContactIds.Contains(targetId),
            ServiceTargetKind.Group => payloadGroupIds.Contains(targetId) || existingGroupIds.Contains(targetId),
            _ => false
        };

    private async Task<int> AddMissingAuditLogsAsync(
        IReadOnlyCollection<AuditLog> source,
        CancellationToken cancellationToken)
    {
        var sourceById = source.ToDictionary(x => x.Id);
        var existing = await db.AuditLogs.AsNoTracking()
            .Where(x => sourceById.Keys.Contains(x.Id))
            .ToListAsync(cancellationToken);
        if (existing.Any(x =>
                !sourceById.TryGetValue(x.Id, out var restored) ||
                !audit.HasValidIntegrity(x) ||
                !AuditLogsEqual(x, restored)))
        {
            throw DomainException.Conflict(
                "backup_audit_conflict",
                "A backup audit record conflicts with the authoritative audit history; restore was not started.");
        }

        var existingIds = existing.Select(x => x.Id).ToHashSet();
        var missing = source.Where(x => !existingIds.Contains(x.Id)).ToList();
        if (missing.Count > 0) await db.AuditLogs.AddRangeAsync(missing, cancellationToken);
        return missing.Count;
    }

    private static bool AuditLogsEqual(AuditLog left, AuditLog right) =>
        left.Id == right.Id &&
        left.TenantId == right.TenantId &&
        left.CreatedAt == right.CreatedAt &&
        string.Equals(left.Actor, right.Actor, StringComparison.Ordinal) &&
        string.Equals(left.Action, right.Action, StringComparison.Ordinal) &&
        string.Equals(left.ResourceType, right.ResourceType, StringComparison.Ordinal) &&
        string.Equals(left.ResourceId, right.ResourceId, StringComparison.Ordinal) &&
        left.Success == right.Success &&
        string.Equals(left.IpAddress, right.IpAddress, StringComparison.Ordinal) &&
        string.Equals(left.CorrelationId, right.CorrelationId, StringComparison.Ordinal) &&
        string.Equals(left.DetailsJson, right.DetailsJson, StringComparison.Ordinal);

    private async Task<byte[]> ReadBackupAsync(BackupManifest manifest, CancellationToken cancellationToken)
    {
        var path = ResolvePath(manifest.FileName);
        if (!File.Exists(path)) throw DomainException.NotFound("Backup payload");
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private string ResolvePath(string fileName)
    {
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
            throw new InvalidOperationException("Backup manifest contains an invalid file name.");
        var root = Path.GetFullPath(options.Value.Directory);
        var resolved = Path.GetFullPath(Path.Combine(root, fileName));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backup path escaped the configured backup directory.");
        return resolved;
    }

    private byte[] GetEncryptionKey()
    {
        try
        {
            var key = Convert.FromBase64String(options.Value.EncryptionKeyBase64);
            if (key.Length != 32) throw new FormatException();
            return key;
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Backup encryption key must be a base64-encoded 32-byte key.");
        }
    }

    private static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Magic);
        var output = new byte[Magic.Length + nonce.Length + tag.Length + ciphertext.Length];
        Magic.CopyTo(output, 0);
        nonce.CopyTo(output, Magic.Length);
        tag.CopyTo(output, Magic.Length + nonce.Length);
        ciphertext.CopyTo(output, Magic.Length + nonce.Length + tag.Length);
        return output;
    }

    private static byte[] Decrypt(byte[] encrypted, byte[] key)
    {
        const int nonceLength = 12;
        const int tagLength = 16;
        if (encrypted.Length < Magic.Length + nonceLength + tagLength ||
            !encrypted.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("Invalid backup envelope.");
        var nonce = encrypted.AsSpan(Magic.Length, nonceLength);
        var tag = encrypted.AsSpan(Magic.Length + nonceLength, tagLength);
        var ciphertext = encrypted.AsSpan(Magic.Length + nonceLength + tagLength);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, tagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, Magic);
        return plaintext;
    }

    private static bool HashEquals(string expected, string actual)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static Dictionary<string, int> CalculateCounts(LogicalBackupPayload payload)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["contacts"] = payload.Contacts.Count,
            ["groups"] = payload.Groups.Count,
            ["remarkRules"] = payload.RemarkRules.Count,
            ["remarkTasks"] = payload.RemarkTasks.Count,
            ["groupMentions"] = payload.GroupMentions.Count,
            ["entitlements"] = payload.Entitlements.Count,
            ["entitlementLedger"] = payload.EntitlementLedger.Count,
            ["activationCodes"] = payload.ActivationCodes.Count,
            ["auditLogs"] = payload.AuditLogs.Count
        };
        if (payload.SchemaVersion >= 2)
        {
            counts["agentRegistrations"] = payload.AgentRegistrations.Count;
        }
        if (payload.SchemaVersion >= 3)
        {
            counts["servicePackages"] = payload.ServicePackages.Count;
        }
        return counts;
    }

    private static bool IsSupportedSchema(int schemaVersion) =>
        schemaVersion is >= LogicalBackupPayload.MinimumSupportedSchemaVersion and <= LogicalBackupPayload.CurrentSchemaVersion;

    private static bool CountsEqual(
        IReadOnlyDictionary<string, int> expected,
        IReadOnlyDictionary<string, int> actual) =>
        expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
