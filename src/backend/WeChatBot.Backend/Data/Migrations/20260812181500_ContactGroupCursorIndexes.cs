using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WeChatBot.Backend.Data.Migrations;

/// <summary>
/// 为联系人和群的稳定游标分页添加与租户过滤及复合排序一致的覆盖索引。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260812181500_ContactGroupCursorIndexes")]
public partial class ContactGroupCursorIndexes : Migration
{
    /// <summary>
    /// 创建联系人和群分页索引，使键集条件可从当前租户的排序位置继续扫描。
    /// </summary>
    /// <param name="migrationBuilder">EF Core 提供的迁移操作构建器。</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // 索引列顺序与全局租户过滤、DisplayName 主排序、Id 决胜排序完全一致。
        migrationBuilder.CreateIndex(
            name: "IX_Contacts_TenantId_DisplayName_Id",
            table: "Contacts",
            columns: new[] { "TenantId", "DisplayName", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_Groups_TenantId_DisplayName_Id",
            table: "Groups",
            columns: new[] { "TenantId", "DisplayName", "Id" });
    }

    /// <summary>
    /// 删除本迁移创建的分页索引，不影响原有外部 ID 唯一约束。
    /// </summary>
    /// <param name="migrationBuilder">EF Core 提供的迁移操作构建器。</param>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Contacts_TenantId_DisplayName_Id",
            table: "Contacts");

        migrationBuilder.DropIndex(
            name: "IX_Groups_TenantId_DisplayName_Id",
            table: "Groups");
    }
}
