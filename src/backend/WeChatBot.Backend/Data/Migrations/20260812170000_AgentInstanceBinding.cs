using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WeChatBot.Backend.Data.Migrations;

/// <inheritdoc />
[DbContext(typeof(AppDbContext))]
[Migration("20260812170000_AgentInstanceBinding")]
public partial class AgentInstanceBinding : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_AgentRegistrations_TenantId_WeChatInstanceId",
            table: "AgentRegistrations",
            columns: new[] { "TenantId", "WeChatInstanceId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_AgentRegistrations_TenantId_WeChatInstanceId",
            table: "AgentRegistrations");
    }
}
