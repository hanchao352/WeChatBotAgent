using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;
using WeChatBot.Backend.Services;

namespace WeChatBot.Backend.Tests;

/// <summary>
/// 验证租约协议引入目标身份快照后，已有数据库和旧版逻辑备份仍能生成可领取的备注任务。
/// </summary>
public sealed class LegacyRemarkTaskCompatibilityTests
{
    /// <summary>表示租约字段迁移之前的最后一个迁移标识，用于稳定构造真实旧数据库。</summary>
    private const string MigrationBeforeRemarkTaskLeases = "20260812181500_ContactGroupCursorIndexes";

    /// <summary>表示需要验证的租约字段迁移标识，测试只升级到该版本以隔离后续迁移影响。</summary>
    private const string RemarkTaskLeasesMigration = "20260812190000_RemarkTaskLeases";

    /// <summary>表示仍受恢复服务支持且没有备注任务身份快照字段的旧备份模式版本。</summary>
    private const int LegacyBackupSchemaVersion = 3;

    /// <summary>表示逻辑备份加密封装固定使用的 AES-GCM nonce 字节数。</summary>
    private const int BackupNonceLength = 12;

    /// <summary>表示逻辑备份加密封装固定使用的 AES-GCM 认证标签字节数。</summary>
    private const int BackupAuthenticationTagLength = 16;

    /// <summary>表示测试环境配置的备份密钥原始材料，必须与测试应用工厂保持一致。</summary>
    private const string BackupKeyMaterial = "integration-test-backup-key";

    /// <summary>表示旧任务使用的 64 位十六进制请求摘要，满足当前字段长度约束。</summary>
    private const string LegacyRequestHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>表示旧载荷已保存的非空外部标识，用于证明兼容补值不会覆盖历史快照。</summary>
    private const string PreservedLegacyExternalId = "preserved-legacy-contact-external";

    /// <summary>表示备份文件格式的固定认证头；该值由后端逻辑备份协议定义。</summary>
    private static readonly byte[] BackupMagic = "WXB1"u8.ToArray();

    /// <summary>表示测试应用工厂配置的 32 字节 AES-256 备份密钥。</summary>
    private static readonly byte[] BackupEncryptionKey = SHA256.HashData(Encoding.UTF8.GetBytes(BackupKeyMaterial));

    /// <summary>按 Web API 的 camelCase 约定序列化和解析测试请求及响应。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 验证真实 SQLite 迁移会分别从联系人和群回填身份快照，并且不会跨租户借用同 ID 的目标。
    /// </summary>
    [Fact]
    public async Task Remark_task_lease_migration_backfills_legacy_contact_and_group_identity_snapshots()
    {
        // 临时目录只承载本测试的 SQLite 文件，测试结束后可整体回收。
        var databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "wechatbot-legacy-migration-tests",
            Guid.NewGuid().ToString("N"));
        // 数据库路径使用唯一目录，避免并行测试之间共享迁移历史或文件锁。
        var databasePath = Path.Combine(databaseDirectory, "legacy.db");
        Directory.CreateDirectory(databaseDirectory);

        try
        {
            // 主租户包含合法联系人和群任务，外租户任务故意引用主租户目标以验证租户条件。
            var tenantId = Guid.NewGuid();
            var foreignTenantId = Guid.NewGuid();
            // 目标标识覆盖联系人和群两种枚举分支。
            var contactId = Guid.NewGuid();
            var groupId = Guid.NewGuid();
            // 每项任务使用独立规则，满足旧表上的规则外键约束。
            var contactRuleId = Guid.NewGuid();
            var groupRuleId = Guid.NewGuid();
            var foreignRuleId = Guid.NewGuid();
            // 任务标识用于迁移后精确核对各分支的持久化结果。
            var contactTaskId = Guid.NewGuid();
            var groupTaskId = Guid.NewGuid();
            var crossTenantTaskId = Guid.NewGuid();

            await using (var legacyDatabase = CreateMigrationContext(databasePath, tenantId))
            {
                // 先执行到目标迁移之前，确保插入的任务确实没有身份快照列。
                await legacyDatabase.Database.MigrateAsync(MigrationBeforeRemarkTaskLeases);
                await InsertLegacyContactAsync(
                    legacyDatabase,
                    contactId,
                    tenantId,
                    "legacy-contact-external",
                    "旧联系人");
                await InsertLegacyGroupAsync(
                    legacyDatabase,
                    groupId,
                    tenantId,
                    "legacy-group-external",
                    "旧服务群");
                await InsertLegacyRuleAsync(legacyDatabase, contactRuleId, tenantId, ServiceTargetKind.Contact);
                await InsertLegacyRuleAsync(legacyDatabase, groupRuleId, tenantId, ServiceTargetKind.Group);
                await InsertLegacyRuleAsync(legacyDatabase, foreignRuleId, foreignTenantId, ServiceTargetKind.Contact);
                await InsertLegacyTaskAsync(
                    legacyDatabase,
                    contactTaskId,
                    tenantId,
                    contactRuleId,
                    ServiceTargetKind.Contact,
                    contactId);
                await InsertLegacyTaskAsync(
                    legacyDatabase,
                    groupTaskId,
                    tenantId,
                    groupRuleId,
                    ServiceTargetKind.Group,
                    groupId);
                await InsertLegacyTaskAsync(
                    legacyDatabase,
                    crossTenantTaskId,
                    foreignTenantId,
                    foreignRuleId,
                    ServiceTargetKind.Contact,
                    contactId);

                // 通过 EF Core 10 的迁移执行器运行生产迁移，直接验证 SQLite 实际生成并执行的 SQL。
                await legacyDatabase.Database.MigrateAsync(RemarkTaskLeasesMigration);
            }

            await using var verificationDatabase = CreateMigrationContext(databasePath, tenantId);
            // 忽略全局过滤器后一次读取全部三个任务，确保租户边界断言不会被查询过滤掩盖。
            var migratedTasks = await verificationDatabase.RemarkTasks
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(task => new[] { contactTaskId, groupTaskId, crossTenantTaskId }.Contains(task.Id))
                .ToDictionaryAsync(task => task.Id);

            Assert.Equal("legacy-contact-external", migratedTasks[contactTaskId].TargetExternalId);
            Assert.Equal("旧联系人", migratedTasks[contactTaskId].ExpectedTargetDisplayName);
            Assert.Equal("legacy-group-external", migratedTasks[groupTaskId].TargetExternalId);
            Assert.Equal("旧服务群", migratedTasks[groupTaskId].ExpectedTargetDisplayName);
            Assert.Equal(string.Empty, migratedTasks[crossTenantTaskId].TargetExternalId);
            Assert.Equal(string.Empty, migratedTasks[crossTenantTaskId].ExpectedTargetDisplayName);
        }
        finally
        {
            // 目录由测试创建且路径包含随机 GUID；释放上下文后只删除该精确目录。
            if (Directory.Exists(databaseDirectory)) Directory.Delete(databaseDirectory, true);
        }
    }

    /// <summary>
    /// 验证 schema v3 任务缺失身份快照时，联系人优先采用备份目标，备份未包含的群采用当前数据库目标。
    /// </summary>
    [Fact]
    public async Task Schema_v3_restore_populates_missing_identity_snapshots_from_backup_and_current_database()
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        // 两类目标及任务直接写入数据库，避免接口权益门禁干扰备份兼容性验证。
        var fixture = await CreateBackupFixtureAsync(factory);
        var backup = await CreateBackupAsync(admin, "legacy identity snapshot compatibility");

        // schema v3 不包含任务身份字段，并故意移除群集合项以覆盖当前数据库回退路径。
        await RewriteAsLegacyBackupAsync(
            factory,
            backup.Id,
            excludedContactIds: new HashSet<Guid>(),
            excludedGroupIds: new HashSet<Guid> { fixture.GroupId },
            preservedExternalIdTaskId: fixture.ContactTaskId);

        // 联系人会被备份值覆盖，群未进入备份，应保留并使用下面设置的当前数据库值。
        const string currentContactExternalId = "current-contact-after-backup";
        const string currentContactDisplayName = "备份后的联系人名称";
        const string currentGroupExternalId = "current-group-after-backup";
        const string currentGroupDisplayName = "当前数据库群名称";
        using (var mutationScope = factory.Services.CreateScope())
        {
            var database = mutationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            // 后台服务作用域没有请求用户，测试维护操作需显式忽略租户查询过滤器。
            var contact = await database.Contacts.IgnoreQueryFilters()
                .SingleAsync(item => item.Id == fixture.ContactId);
            var group = await database.Groups.IgnoreQueryFilters()
                .SingleAsync(item => item.Id == fixture.GroupId);
            contact.ExternalId = currentContactExternalId;
            contact.DisplayName = currentContactDisplayName;
            contact.Version++;
            group.ExternalId = currentGroupExternalId;
            group.DisplayName = currentGroupDisplayName;
            group.Version++;
            await database.RemarkTasks.IgnoreQueryFilters()
                .Where(task => new[] { fixture.ContactTaskId, fixture.GroupTaskId }.Contains(task.Id))
                .ExecuteDeleteAsync();
            await database.SaveChangesAsync();
        }

        using var restoreResponse = await RestoreBackupAsync(admin, backup.Id);
        var restoreBody = await restoreResponse.Content.ReadAsStringAsync();
        Assert.True(restoreResponse.IsSuccessStatusCode, restoreBody);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDatabase = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var restoredTasks = await verificationDatabase.RemarkTasks.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(task => new[] { fixture.ContactTaskId, fixture.GroupTaskId }.Contains(task.Id))
            .ToDictionaryAsync(task => task.Id);

        // 联系人存在于备份集合，因此恢复后的任务必须使用备份时的稳定身份，而不是恢复前数据库中的新值。
        Assert.Equal(PreservedLegacyExternalId, restoredTasks[fixture.ContactTaskId].TargetExternalId);
        Assert.Equal(fixture.ContactDisplayName, restoredTasks[fixture.ContactTaskId].ExpectedTargetDisplayName);
        // 群不在备份集合但仍存在于当前租户数据库，因此旧任务必须采用当前可信身份。
        Assert.Equal(currentGroupExternalId, restoredTasks[fixture.GroupTaskId].TargetExternalId);
        Assert.Equal(currentGroupDisplayName, restoredTasks[fixture.GroupTaskId].ExpectedTargetDisplayName);
    }

    /// <summary>
    /// 验证旧备份任务的目标既不在备份集合也不在当前租户数据库时，恢复会在写入前失败关闭。
    /// </summary>
    [Fact]
    public async Task Schema_v3_restore_rejects_a_remark_task_whose_target_is_unavailable()
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        var fixture = await CreateBackupFixtureAsync(factory);
        var backup = await CreateBackupAsync(admin, "legacy missing target compatibility");

        // 从备份联系人集合移除目标，使恢复只能尝试当前数据库回退。
        await RewriteAsLegacyBackupAsync(
            factory,
            backup.Id,
            excludedContactIds: new HashSet<Guid> { fixture.ContactId },
            excludedGroupIds: new HashSet<Guid>());

        using (var deletionScope = factory.Services.CreateScope())
        {
            var database = deletionScope.ServiceProvider.GetRequiredService<AppDbContext>();
            // 先删除任务再删除目标，构造不存在任何可用身份来源的恢复输入。
            await database.RemarkTasks.IgnoreQueryFilters()
                .Where(task => task.Id == fixture.ContactTaskId)
                .ExecuteDeleteAsync();
            await database.Contacts.IgnoreQueryFilters()
                .Where(contact => contact.Id == fixture.ContactId)
                .ExecuteDeleteAsync();
        }

        using var restoreResponse = await RestoreBackupAsync(admin, backup.Id);
        var restoreBody = await restoreResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, restoreResponse.StatusCode);
        Assert.Contains("backup_reference_integrity_failed", restoreBody, StringComparison.Ordinal);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDatabase = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verificationDatabase.RemarkTasks.IgnoreQueryFilters()
            .AnyAsync(task => task.Id == fixture.ContactTaskId));
    }

    /// <summary>
    /// 验证 schema v4 缺失任务身份快照时不会启用旧版本补值，而是在修改业务数据前拒绝恢复。
    /// </summary>
    [Fact]
    public async Task Schema_v4_restore_keeps_strict_identity_snapshot_validation()
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        var fixture = await CreateBackupFixtureAsync(factory);
        var backup = await CreateBackupAsync(admin, "strict version four identity validation");

        // 保持清单与载荷的 v4 版本，仅删除任务身份字段，证明兼容路径不会掩盖损坏的当前格式。
        await RewriteBackupIdentitySnapshotsAsync(
            factory,
            backup.Id,
            schemaVersion: 4,
            excludedContactIds: new HashSet<Guid>(),
            excludedGroupIds: new HashSet<Guid>());
        using (var deletionScope = factory.Services.CreateScope())
        {
            var database = deletionScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await database.RemarkTasks.IgnoreQueryFilters()
                .Where(task => task.Id == fixture.ContactTaskId)
                .ExecuteDeleteAsync();
        }

        using var restoreResponse = await RestoreBackupAsync(admin, backup.Id);
        var restoreBody = await restoreResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, restoreResponse.StatusCode);
        Assert.Contains("backup_remark_task_identity_invalid", restoreBody, StringComparison.Ordinal);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDatabase = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verificationDatabase.RemarkTasks.IgnoreQueryFilters()
            .AnyAsync(task => task.Id == fixture.ContactTaskId));
    }

    /// <summary>
    /// 验证旧备份目标存在但稳定外部标识为空时，恢复会拒绝生成无法可靠定位的任务。
    /// </summary>
    [Fact]
    public async Task Schema_v3_restore_rejects_an_incomplete_target_identity()
    {
        using var factory = new TestApplicationFactory();
        using var admin = factory.CreateAuthenticatedClient();
        var fixture = await CreateBackupFixtureAsync(factory);
        var backup = await CreateBackupAsync(admin, "legacy incomplete target identity");

        // 保留联系人目标引用但清空其外部 ID，使引用完整性通过而身份完整性单独失败。
        await RewriteBackupIdentitySnapshotsAsync(
            factory,
            backup.Id,
            LegacyBackupSchemaVersion,
            excludedContactIds: new HashSet<Guid>(),
            excludedGroupIds: new HashSet<Guid>(),
            emptyExternalIdContactId: fixture.ContactId);
        using (var deletionScope = factory.Services.CreateScope())
        {
            var database = deletionScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await database.RemarkTasks.IgnoreQueryFilters()
                .Where(task => task.Id == fixture.ContactTaskId)
                .ExecuteDeleteAsync();
        }

        using var restoreResponse = await RestoreBackupAsync(admin, backup.Id);
        var restoreBody = await restoreResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, restoreResponse.StatusCode);
        Assert.Contains("backup_remark_task_identity_invalid", restoreBody, StringComparison.Ordinal);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDatabase = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verificationDatabase.RemarkTasks.IgnoreQueryFilters()
            .AnyAsync(task => task.Id == fixture.ContactTaskId));
    }

    /// <summary>
    /// 创建只用于迁移验证的上下文；无 HTTP 用户时由 Auth 配置提供预期租户，查询时可显式忽略过滤器。
    /// </summary>
    /// <param name="databasePath">临时 SQLite 数据库的绝对路径。</param>
    /// <param name="tenantId">上下文保存校验和默认查询过滤器使用的租户标识。</param>
    /// <returns>指向指定数据库且使用生产模型的上下文。</returns>
    private static AppDbContext CreateMigrationContext(string databasePath, Guid tenantId)
    {
        // 迁移测试必须使用项目实际配置的 SQLite 提供程序，不能用内存提供程序替代 SQL 执行。
        var databaseOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        // 空 HTTP 上下文使 TenantContext 返回空租户，AppDbContext 会回退到这里的 Auth 租户配置。
        var tenantContext = new TenantContext(new HttpContextAccessor());
        var authenticationOptions = Options.Create(new AuthOptions { TenantId = tenantId });
        return new AppDbContext(databaseOptions, tenantContext, authenticationOptions);
    }

    /// <summary>向旧数据库插入一个联系人目标，字段集合与租约迁移之前的表结构一致。</summary>
    /// <param name="database">已迁移到旧版本的数据库上下文。</param>
    /// <param name="contactId">联系人主键。</param>
    /// <param name="tenantId">联系人所属租户。</param>
    /// <param name="externalId">迁移后应进入任务快照的稳定外部标识。</param>
    /// <param name="displayName">迁移后应进入任务快照的显示名称。</param>
    private static Task<int> InsertLegacyContactAsync(
        AppDbContext database,
        Guid contactId,
        Guid tenantId,
        string externalId,
        string displayName)
    {
        // SQLite 日期转换器使用 UTC ticks；固定同一时刻即可满足非空创建和更新时间列。
        var timestamp = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        return database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Contacts
                (Id, TenantId, ExternalId, DisplayName, WeChatId, CustomerCode, SystemRemark,
                 CurrentWeChatRemark, ManualRemarkProtected, ServiceExpiresAt, CreatedAt, UpdatedAt, Version)
            VALUES
                ({contactId}, {tenantId}, {externalId}, {displayName}, NULL, NULL, NULL,
                 NULL, 0, NULL, {timestamp}, {timestamp}, 1)
            """);
    }

    /// <summary>向旧数据库插入一个群目标，字段集合与租约迁移之前的表结构一致。</summary>
    /// <param name="database">已迁移到旧版本的数据库上下文。</param>
    /// <param name="groupId">群主键。</param>
    /// <param name="tenantId">群所属租户。</param>
    /// <param name="externalId">迁移后应进入任务快照的稳定外部标识。</param>
    /// <param name="displayName">迁移后应进入任务快照的显示名称。</param>
    private static Task<int> InsertLegacyGroupAsync(
        AppDbContext database,
        Guid groupId,
        Guid tenantId,
        string externalId,
        string displayName)
    {
        var timestamp = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        return database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Groups
                (Id, TenantId, ExternalId, DisplayName, BusinessName, SystemRemark,
                 CurrentWeChatRemark, ManualRemarkProtected, ServiceExpiresAt, CreatedAt, UpdatedAt, Version)
            VALUES
                ({groupId}, {tenantId}, {externalId}, {displayName}, NULL, NULL,
                 NULL, 0, NULL, {timestamp}, {timestamp}, 1)
            """);
    }

    /// <summary>向旧数据库插入与指定目标类型一致的备注规则，以满足任务规则外键。</summary>
    /// <param name="database">已迁移到旧版本的数据库上下文。</param>
    /// <param name="ruleId">规则主键。</param>
    /// <param name="tenantId">规则所属租户。</param>
    /// <param name="targetKind">规则支持的目标类型。</param>
    private static Task<int> InsertLegacyRuleAsync(
        AppDbContext database,
        Guid ruleId,
        Guid tenantId,
        ServiceTargetKind targetKind)
    {
        var timestamp = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        var targetKindValue = targetKind.ToString();
        var ruleName = $"legacy-rule-{ruleId:N}";
        // 模板作为 SQL 参数传入，既避免 raw string 大括号歧义，也保持测试与生产参数化写入一致。
        const string template = "{displayName}";
        return database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO RemarkRules
                (Id, TenantId, Name, TargetKind, Template, ConflictPolicy, IsEnabled,
                 MaxLength, CreatedAt, UpdatedAt, Version)
            VALUES
                ({ruleId}, {tenantId}, {ruleName}, {targetKindValue}, {template},
                 'OverwriteSystemGeneratedOnly', 1, 64, {timestamp}, {timestamp}, 1)
            """);
    }

    /// <summary>向旧数据库插入不含目标身份快照列的待处理备注任务。</summary>
    /// <param name="database">已迁移到旧版本的数据库上下文。</param>
    /// <param name="taskId">任务主键。</param>
    /// <param name="tenantId">任务所属租户。</param>
    /// <param name="ruleId">任务引用的规则主键。</param>
    /// <param name="targetKind">联系人或群目标类型。</param>
    /// <param name="targetId">任务引用的目标主键。</param>
    private static Task<int> InsertLegacyTaskAsync(
        AppDbContext database,
        Guid taskId,
        Guid tenantId,
        Guid ruleId,
        ServiceTargetKind targetKind,
        Guid targetId)
    {
        var timestamp = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        var targetKindValue = targetKind.ToString();
        var idempotencyKey = $"legacy-task-{taskId:N}";
        return database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO RemarkTasks
                (Id, TenantId, RuleId, TargetKind, TargetId, IdempotencyKey, RequestHash,
                 GeneratedRemark, OriginalSystemRemark, OriginalWeChatRemark, Status,
                 ConflictReason, FailureReason, CreatedAt, CompletedAt, Version)
            VALUES
                ({taskId}, {tenantId}, {ruleId}, {targetKindValue}, {targetId}, {idempotencyKey},
                 {LegacyRequestHash}, 'legacy-generated-remark', NULL, NULL, 'Pending',
                 NULL, NULL, {timestamp}, NULL, 1)
            """);
    }

    /// <summary>创建同时包含联系人任务和群任务的逻辑备份测试数据。</summary>
    /// <param name="factory">提供隔离数据库和依赖注入容器的测试应用工厂。</param>
    /// <returns>备份改写与恢复断言所需的目标、任务和原始身份。</returns>
    private static async Task<BackupFixture> CreateBackupFixtureAsync(TestApplicationFactory factory)
    {
        var now = DateTimeOffset.UtcNow;
        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            TenantId = TestApplicationFactory.TenantId,
            ExternalId = $"backup-contact-{Guid.NewGuid():N}",
            DisplayName = "备份联系人",
            CreatedAt = now,
            UpdatedAt = now
        };
        var group = new GroupChat
        {
            Id = Guid.NewGuid(),
            TenantId = TestApplicationFactory.TenantId,
            ExternalId = $"backup-group-{Guid.NewGuid():N}",
            DisplayName = "备份服务群",
            CreatedAt = now,
            UpdatedAt = now
        };
        var contactRule = CreateRemarkRule(ServiceTargetKind.Contact, now);
        var groupRule = CreateRemarkRule(ServiceTargetKind.Group, now);
        var contactTask = CreateRemarkTask(contactRule.Id, ServiceTargetKind.Contact, contact, now);
        var groupTask = CreateRemarkTask(groupRule.Id, ServiceTargetKind.Group, group, now);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        database.Contacts.Add(contact);
        database.Groups.Add(group);
        database.RemarkRules.AddRange(contactRule, groupRule);
        database.RemarkTasks.AddRange(contactTask, groupTask);
        await database.SaveChangesAsync();

        return new BackupFixture(
            contact.Id,
            contactTask.Id,
            contact.DisplayName,
            group.Id,
            groupTask.Id);
    }

    /// <summary>创建与指定目标类型匹配且可持久化的备注规则。</summary>
    /// <param name="targetKind">规则支持的目标类型。</param>
    /// <param name="now">规则的统一创建和更新时间。</param>
    /// <returns>尚未加入上下文的新规则。</returns>
    private static RemarkRule CreateRemarkRule(ServiceTargetKind targetKind, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TestApplicationFactory.TenantId,
        Name = $"legacy-backup-rule-{targetKind}-{Guid.NewGuid():N}",
        TargetKind = targetKind,
        Template = "{displayName}",
        ConflictPolicy = RemarkConflictPolicy.OverwriteSystemGeneratedOnly,
        IsEnabled = true,
        MaxLength = 64,
        CreatedAt = now,
        UpdatedAt = now
    };

    /// <summary>创建包含当前 v4 身份快照、稍后会被改写为旧备份格式的待处理联系人任务。</summary>
    /// <param name="ruleId">任务引用的规则标识。</param>
    /// <param name="targetKind">任务目标类型。</param>
    /// <param name="contact">联系人目标；群任务传入时为空。</param>
    /// <param name="now">任务创建时间。</param>
    /// <returns>尚未加入上下文的新联系人任务。</returns>
    private static RemarkTask CreateRemarkTask(
        Guid ruleId,
        ServiceTargetKind targetKind,
        Contact contact,
        DateTimeOffset now) => CreateRemarkTaskCore(
            ruleId,
            targetKind,
            contact.Id,
            contact.ExternalId,
            contact.DisplayName,
            now);

    /// <summary>创建包含当前 v4 身份快照、稍后会被改写为旧备份格式的待处理群任务。</summary>
    /// <param name="ruleId">任务引用的规则标识。</param>
    /// <param name="targetKind">任务目标类型。</param>
    /// <param name="group">群目标。</param>
    /// <param name="now">任务创建时间。</param>
    /// <returns>尚未加入上下文的新群任务。</returns>
    private static RemarkTask CreateRemarkTask(
        Guid ruleId,
        ServiceTargetKind targetKind,
        GroupChat group,
        DateTimeOffset now) => CreateRemarkTaskCore(
            ruleId,
            targetKind,
            group.Id,
            group.ExternalId,
            group.DisplayName,
            now);

    /// <summary>集中构造联系人和群共用的备注任务字段，避免两类测试数据产生契约差异。</summary>
    /// <param name="ruleId">任务引用的规则标识。</param>
    /// <param name="targetKind">联系人或群目标类型。</param>
    /// <param name="targetId">目标主键。</param>
    /// <param name="targetExternalId">目标稳定外部标识。</param>
    /// <param name="targetDisplayName">目标显示名称。</param>
    /// <param name="now">任务创建时间。</param>
    /// <returns>包含完整身份快照的待处理任务。</returns>
    private static RemarkTask CreateRemarkTaskCore(
        Guid ruleId,
        ServiceTargetKind targetKind,
        Guid targetId,
        string targetExternalId,
        string targetDisplayName,
        DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = TestApplicationFactory.TenantId,
            RuleId = ruleId,
            TargetKind = targetKind,
            TargetId = targetId,
            TargetExternalId = targetExternalId,
            ExpectedTargetDisplayName = targetDisplayName,
            IdempotencyKey = $"legacy-backup-task-{Guid.NewGuid():N}",
            RequestHash = LegacyRequestHash,
            GeneratedRemark = targetDisplayName,
            Status = RemarkTaskStatus.Pending,
            CreatedAt = now
        };

    /// <summary>通过公开管理接口创建一份真实加密逻辑备份。</summary>
    /// <param name="admin">已携带管理员 API Key 的客户端。</param>
    /// <param name="reason">写入备份审计的测试原因。</param>
    /// <returns>包含备份标识的接口响应。</returns>
    private static async Task<BackupResponse> CreateBackupAsync(HttpClient admin, string reason)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/backups")
        {
            Content = JsonContent.Create(new CreateBackupRequest(reason), options: JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", $"legacy-backup-{Guid.NewGuid():N}");
        using var response = await admin.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, responseBody);
        return JsonSerializer.Deserialize<BackupResponse>(responseBody, JsonOptions)!;
    }

    /// <summary>将真实 v4 备份改写成缺少任务身份字段的 schema v3 载荷，并可移除指定目标。</summary>
    /// <param name="factory">用于定位备份清单和文件的测试应用工厂。</param>
    /// <param name="backupId">待改写备份标识。</param>
    /// <param name="excludedContactIds">不得写回旧备份联系人集合的目标标识。</param>
    /// <param name="excludedGroupIds">不得写回旧备份群集合的目标标识。</param>
    /// <param name="preservedExternalIdTaskId">需要保留非空历史外部标识的任务；为空时所有任务均移除该字段。</param>
    private static async Task RewriteAsLegacyBackupAsync(
        TestApplicationFactory factory,
        Guid backupId,
        IReadOnlySet<Guid> excludedContactIds,
        IReadOnlySet<Guid> excludedGroupIds,
        Guid? preservedExternalIdTaskId = null) =>
        await RewriteBackupIdentitySnapshotsAsync(
            factory,
            backupId,
            LegacyBackupSchemaVersion,
            excludedContactIds,
            excludedGroupIds,
            preservedExternalIdTaskId);

    /// <summary>改写逻辑备份的模式版本、任务身份字段和可选目标集合，并同步清单元数据。</summary>
    /// <param name="factory">用于定位备份清单和文件的测试应用工厂。</param>
    /// <param name="backupId">待改写备份标识。</param>
    /// <param name="schemaVersion">写入载荷和清单的模式版本。</param>
    /// <param name="excludedContactIds">不得写回联系人集合的目标标识。</param>
    /// <param name="excludedGroupIds">不得写回群集合的目标标识。</param>
    /// <param name="preservedExternalIdTaskId">需要保留非空历史外部标识的任务。</param>
    /// <param name="emptyExternalIdContactId">需要模拟空外部 ID 的联系人目标。</param>
    private static async Task RewriteBackupIdentitySnapshotsAsync(
        TestApplicationFactory factory,
        Guid backupId,
        int schemaVersion,
        IReadOnlySet<Guid> excludedContactIds,
        IReadOnlySet<Guid> excludedGroupIds,
        Guid? preservedExternalIdTaskId = null,
        Guid? emptyExternalIdContactId = null)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manifest = await database.BackupManifests.IgnoreQueryFilters()
            .SingleAsync(item => item.Id == backupId);
        var backupPath = Path.Combine(factory.BackupDirectory, manifest.FileName);
        var encryptedPayload = await File.ReadAllBytesAsync(backupPath);
        var plaintextPayload = DecryptBackup(encryptedPayload);

        using var document = JsonDocument.Parse(plaintextPayload);
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
                else if (property.NameEquals("remarkTasks"))
                {
                    WriteLegacyRemarkTasks(writer, property.Value, preservedExternalIdTaskId);
                }
                else if (property.NameEquals("contacts"))
                {
                    WriteFilteredTargets(writer, property.Value, excludedContactIds, emptyExternalIdContactId);
                }
                else if (property.NameEquals("groups"))
                {
                    WriteFilteredTargets(writer, property.Value, excludedGroupIds, emptyExternalIdTargetId: null);
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        var rewrittenPayload = EncryptBackup(output.ToArray());
        await File.WriteAllBytesAsync(backupPath, rewrittenPayload);
        manifest.SchemaVersion = schemaVersion;
        manifest.PayloadSha256 = StableHash.Sha256(rewrittenPayload);
        manifest.Bytes = rewrittenPayload.LongLength;
        var manifestCounts = JsonSerializer.Deserialize<Dictionary<string, int>>(manifest.CountsJson, JsonOptions)!;
        manifestCounts["contacts"] -= excludedContactIds.Count;
        manifestCounts["groups"] -= excludedGroupIds.Count;
        manifest.CountsJson = JsonSerializer.Serialize(manifestCounts, JsonOptions);
        await database.SaveChangesAsync();
    }

    /// <summary>序列化 schema v3 备注任务，并从每项任务中移除 v4 才引入的两项身份快照。</summary>
    /// <param name="writer">目标 UTF-8 JSON 写入器。</param>
    /// <param name="tasks">原始 v4 备注任务数组。</param>
    /// <param name="preservedExternalIdTaskId">需要保留非空外部标识的任务；用于验证逐字段补值语义。</param>
    private static void WriteLegacyRemarkTasks(
        Utf8JsonWriter writer,
        JsonElement tasks,
        Guid? preservedExternalIdTaskId)
    {
        writer.WriteStartArray();
        foreach (var task in tasks.EnumerateArray())
        {
            // 任务主键决定是否模拟“旧载荷已携带一项非空快照”的兼容输入。
            var taskId = task.GetProperty("id").GetGuid();
            writer.WriteStartObject();
            foreach (var property in task.EnumerateObject())
            {
                if (property.NameEquals("targetExternalId"))
                {
                    if (taskId == preservedExternalIdTaskId)
                    {
                        writer.WriteString(property.Name, PreservedLegacyExternalId);
                    }
                    continue;
                }
                if (property.NameEquals("expectedTargetDisplayName"))
                {
                    continue;
                }
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    /// <summary>写入目标集合并按主键排除测试指定项，保持其余 JSON 字段原样不变。</summary>
    /// <param name="writer">目标 UTF-8 JSON 写入器。</param>
    /// <param name="targets">联系人或群的 JSON 数组。</param>
    /// <param name="excludedIds">不应进入改写后数组的目标标识。</param>
    /// <param name="emptyExternalIdTargetId">需要保留但将外部 ID 改为空字符串的目标标识。</param>
    private static void WriteFilteredTargets(
        Utf8JsonWriter writer,
        JsonElement targets,
        IReadOnlySet<Guid> excludedIds,
        Guid? emptyExternalIdTargetId)
    {
        writer.WriteStartArray();
        foreach (var target in targets.EnumerateArray())
        {
            var targetId = target.GetProperty("id").GetGuid();
            if (excludedIds.Contains(targetId)) continue;
            if (targetId != emptyExternalIdTargetId)
            {
                target.WriteTo(writer);
                continue;
            }

            // 只替换外部 ID 字段，其余目标属性保持原样，以隔离身份非空校验行为。
            writer.WriteStartObject();
            foreach (var property in target.EnumerateObject())
            {
                if (property.NameEquals("externalId"))
                {
                    writer.WriteString(property.Name, string.Empty);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    /// <summary>解密测试应用生成的 AES-256-GCM 备份文件，并验证固定文件头和认证标签。</summary>
    /// <param name="encrypted">完整备份文件字节。</param>
    /// <returns>可由 JSON 解析器读取的明文字节。</returns>
    private static byte[] DecryptBackup(byte[] encrypted)
    {
        var nonce = encrypted.AsSpan(BackupMagic.Length, BackupNonceLength);
        var tag = encrypted.AsSpan(BackupMagic.Length + BackupNonceLength, BackupAuthenticationTagLength);
        var ciphertext = encrypted.AsSpan(BackupMagic.Length + BackupNonceLength + BackupAuthenticationTagLength);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(BackupEncryptionKey, BackupAuthenticationTagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, BackupMagic);
        return plaintext;
    }

    /// <summary>按后端 WXB1 封装格式重新加密旧模式 JSON，确保恢复仍经过真实密码学校验。</summary>
    /// <param name="plaintext">改写后的 UTF-8 JSON 明文字节。</param>
    /// <returns>包含文件头、随机 nonce、认证标签和密文的备份字节。</returns>
    private static byte[] EncryptBackup(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(BackupNonceLength);
        var tag = new byte[BackupAuthenticationTagLength];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(BackupEncryptionKey, BackupAuthenticationTagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, BackupMagic);
        var output = new byte[BackupMagic.Length + nonce.Length + tag.Length + ciphertext.Length];
        BackupMagic.CopyTo(output, 0);
        nonce.CopyTo(output, BackupMagic.Length);
        tag.CopyTo(output, BackupMagic.Length + nonce.Length);
        ciphertext.CopyTo(output, BackupMagic.Length + nonce.Length + tag.Length);
        return output;
    }

    /// <summary>向公开恢复接口提交确认和唯一幂等键，并返回未释放的原始 HTTP 响应。</summary>
    /// <param name="admin">已携带管理员 API Key 的客户端。</param>
    /// <param name="backupId">待恢复备份标识。</param>
    /// <returns>由调用方读取并释放的恢复响应。</returns>
    private static Task<HttpResponseMessage> RestoreBackupAsync(HttpClient admin, Guid backupId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/backups/{backupId:D}/restore")
        {
            Content = JsonContent.Create(new RestoreBackupRequest("RESTORE"), options: JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", $"legacy-restore-{Guid.NewGuid():N}");
        return admin.SendAsync(request);
    }

    /// <summary>保存一次备份兼容性测试创建的目标、任务及联系人原始身份。</summary>
    /// <param name="ContactId">联系人目标标识。</param>
    /// <param name="ContactTaskId">联系人备注任务标识。</param>
    /// <param name="ContactDisplayName">备份时的联系人显示名称。</param>
    /// <param name="GroupId">群目标标识。</param>
    /// <param name="GroupTaskId">群备注任务标识。</param>
    private sealed record BackupFixture(
        Guid ContactId,
        Guid ContactTaskId,
        string ContactDisplayName,
        Guid GroupId,
        Guid GroupTaskId);

    /// <summary>表示备份创建接口中本测试需要的稳定标识。</summary>
    /// <param name="Id">新建备份的全局唯一标识。</param>
    private sealed record BackupResponse(Guid Id);
}
