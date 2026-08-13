using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WeChatBot.Backend.Data.Migrations;

/// <summary>
/// 为 AgentRegistration 增加独立凭据摘要及签发、轮换、吊销时间，并建立租户内摘要唯一约束。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260812200000_AgentIndependentCredentials")]
public partial class AgentIndependentCredentials : Migration
{
    /// <summary>
    /// 添加全部可空凭据列，使历史注册在升级后默认处于“未签发”状态，并创建唯一摘要索引。
    /// </summary>
    /// <param name="migrationBuilder">EF Core 提供的迁移操作构建器。</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CredentialHash",
            table: "AgentRegistrations",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "CredentialIssuedAt",
            table: "AgentRegistrations",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "CredentialRevokedAt",
            table: "AgentRegistrations",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "CredentialRotatedAt",
            table: "AgentRegistrations",
            type: "INTEGER",
            nullable: true);

        // SQLite 唯一索引允许多个 NULL，因而历史未签发记录可以共存，非空摘要则在租户内必须唯一。
        migrationBuilder.CreateIndex(
            name: "IX_AgentRegistrations_TenantId_CredentialHash",
            table: "AgentRegistrations",
            columns: new[] { "TenantId", "CredentialHash" },
            unique: true);
    }

    /// <summary>删除独立凭据索引和字段，回退到只保存 Agent 注册元数据的旧模型。</summary>
    /// <param name="migrationBuilder">EF Core 提供的迁移操作构建器。</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_AgentRegistrations_TenantId_CredentialHash",
            table: "AgentRegistrations");

        migrationBuilder.DropColumn(name: "CredentialHash", table: "AgentRegistrations");
        migrationBuilder.DropColumn(name: "CredentialIssuedAt", table: "AgentRegistrations");
        migrationBuilder.DropColumn(name: "CredentialRevokedAt", table: "AgentRegistrations");
        migrationBuilder.DropColumn(name: "CredentialRotatedAt", table: "AgentRegistrations");
    }
}
