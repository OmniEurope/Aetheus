using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <summary>Expand API profile settings with NOT NULL defaults so the preceding blue-green
    /// release can still insert profiles (ADR-023). Keep CLI compatibility columns until their
    /// documented contract step.</summary>
    public partial class AddAiRunnerProfileApiSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApiKeyEncrypted",
                table: "AiRunnerProfiles",
                type: "character varying(16000)",
                maxLength: 16000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BaseUrl",
                table: "AiRunnerProfiles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "MaxOutputTokens",
                table: "AiRunnerProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "AiRunnerProfiles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "AiRunnerProfiles",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReasoningEffort",
                table: "AiRunnerProfiles",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApiKeyEncrypted",
                table: "AiRunnerProfiles");

            migrationBuilder.DropColumn(
                name: "BaseUrl",
                table: "AiRunnerProfiles");

            migrationBuilder.DropColumn(
                name: "MaxOutputTokens",
                table: "AiRunnerProfiles");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "AiRunnerProfiles");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "AiRunnerProfiles");

            migrationBuilder.DropColumn(
                name: "ReasoningEffort",
                table: "AiRunnerProfiles");
        }
    }
}
