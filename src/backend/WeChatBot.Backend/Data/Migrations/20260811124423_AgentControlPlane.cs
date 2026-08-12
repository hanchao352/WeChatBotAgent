using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WeChatBot.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentControlPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentRegistrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    NormalizedAgentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WeChatInstanceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConfigurationVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RegisteredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRegistrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentHeartbeatStates",
                columns: table => new
                {
                    AgentRegistrationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RuntimeState = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ChangedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastCommandCompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastCommandCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    QueueDepth = table.Column<int>(type: "INTEGER", nullable: false),
                    ActiveExecutions = table.Column<int>(type: "INTEGER", nullable: false),
                    DryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    AgentVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastRejectedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastRejectedWeChatInstanceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentHeartbeatStates", x => x.AgentRegistrationId);
                    table.ForeignKey(
                        name: "FK_AgentHeartbeatStates_AgentRegistrations_AgentRegistrationId",
                        column: x => x.AgentRegistrationId,
                        principalTable: "AgentRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentHeartbeatStates_TenantId_ReceivedAt",
                table: "AgentHeartbeatStates",
                columns: new[] { "TenantId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRegistrations_TenantId_NormalizedAgentId",
                table: "AgentRegistrations",
                columns: new[] { "TenantId", "NormalizedAgentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentHeartbeatStates");

            migrationBuilder.DropTable(
                name: "AgentRegistrations");
        }
    }
}
