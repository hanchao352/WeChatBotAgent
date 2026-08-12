using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WeChatBot.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class ActivationCodeRevocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RevocationReason",
                table: "ActivationCodes",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RevokedAt",
                table: "ActivationCodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevokedBy",
                table: "ActivationCodes",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "ActivationCodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RevocationReason",
                table: "ActivationCodes");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "ActivationCodes");

            migrationBuilder.DropColumn(
                name: "RevokedBy",
                table: "ActivationCodes");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ActivationCodes");
        }
    }
}
