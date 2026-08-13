using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WeChatBot.Backend.Data.Migrations;

/// <summary>
/// 为备注任务增加目标身份快照、Agent 租约、认领次数与完成结果去重字段，并创建原子领取所需的索引。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260812190000_RemarkTaskLeases")]
public partial class RemarkTaskLeases : Migration
{
    /// <summary>
    /// 添加身份与租约列，回填已有任务的目标身份，并创建领取扫描、完成结果去重索引。
    /// </summary>
    /// <param name="migrationBuilder">EF Core 提供的迁移操作构建器。</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "AttemptCount",
            table: "RemarkTasks",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "ClaimedByAgentId",
            table: "RemarkTasks",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ClaimedWeChatInstanceId",
            table: "RemarkTasks",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CompletionResultId",
            table: "RemarkTasks",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ExpectedTargetDisplayName",
            table: "RemarkTasks",
            type: "TEXT",
            maxLength: 256,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<long>(
            name: "LeaseExpiresAt",
            table: "RemarkTasks",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LeaseTokenHash",
            table: "RemarkTasks",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TargetExternalId",
            table: "RemarkTasks",
            type: "TEXT",
            maxLength: 128,
            nullable: false,
            defaultValue: "");

        // 两类旧任务都只能读取同租户、同类型、同主键的目标；目标不存在时相关子查询返回 NULL，COALESCE 保留安全空值。
        migrationBuilder.Sql("""
            UPDATE RemarkTasks
            SET TargetExternalId = COALESCE(
                    (
                        SELECT Contacts.ExternalId
                        FROM Contacts
                        WHERE RemarkTasks.TargetKind = 'Contact'
                          AND Contacts.TenantId = RemarkTasks.TenantId
                          AND Contacts.Id = RemarkTasks.TargetId
                    ),
                    (
                        SELECT Groups.ExternalId
                        FROM Groups
                        WHERE RemarkTasks.TargetKind = 'Group'
                          AND Groups.TenantId = RemarkTasks.TenantId
                          AND Groups.Id = RemarkTasks.TargetId
                    ),
                    ''),
                ExpectedTargetDisplayName = COALESCE(
                    (
                        SELECT Contacts.DisplayName
                        FROM Contacts
                        WHERE RemarkTasks.TargetKind = 'Contact'
                          AND Contacts.TenantId = RemarkTasks.TenantId
                          AND Contacts.Id = RemarkTasks.TargetId
                    ),
                    (
                        SELECT Groups.DisplayName
                        FROM Groups
                        WHERE RemarkTasks.TargetKind = 'Group'
                          AND Groups.TenantId = RemarkTasks.TenantId
                          AND Groups.Id = RemarkTasks.TargetId
                    ),
                    '')
            WHERE TargetExternalId = ''
               OR ExpectedTargetDisplayName = '';
            """);

        migrationBuilder.CreateIndex(
            name: "IX_RemarkTasks_TenantId_CompletionResultId",
            table: "RemarkTasks",
            columns: new[] { "TenantId", "CompletionResultId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RemarkTasks_TenantId_Status_LeaseExpiresAt_CreatedAt_Id",
            table: "RemarkTasks",
            columns: new[] { "TenantId", "Status", "LeaseExpiresAt", "CreatedAt", "Id" });
    }

    /// <summary>
    /// 删除租约索引和字段，恢复到不支持 Agent 认领协议的模型。
    /// </summary>
    /// <param name="migrationBuilder">EF Core 提供的迁移操作构建器。</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_RemarkTasks_TenantId_CompletionResultId",
            table: "RemarkTasks");

        migrationBuilder.DropIndex(
            name: "IX_RemarkTasks_TenantId_Status_LeaseExpiresAt_CreatedAt_Id",
            table: "RemarkTasks");

        migrationBuilder.DropColumn(name: "AttemptCount", table: "RemarkTasks");
        migrationBuilder.DropColumn(name: "ClaimedByAgentId", table: "RemarkTasks");
        migrationBuilder.DropColumn(name: "ClaimedWeChatInstanceId", table: "RemarkTasks");
        migrationBuilder.DropColumn(name: "CompletionResultId", table: "RemarkTasks");
        migrationBuilder.DropColumn(name: "ExpectedTargetDisplayName", table: "RemarkTasks");
        migrationBuilder.DropColumn(name: "LeaseExpiresAt", table: "RemarkTasks");
        migrationBuilder.DropColumn(name: "LeaseTokenHash", table: "RemarkTasks");
        migrationBuilder.DropColumn(name: "TargetExternalId", table: "RemarkTasks");
    }
}
