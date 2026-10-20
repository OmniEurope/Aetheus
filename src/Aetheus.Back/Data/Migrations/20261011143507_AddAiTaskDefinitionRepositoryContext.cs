using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiTaskDefinitionRepositoryContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContextPathsJson",
                table: "AiTaskDefinitions",
                type: "character varying(16000)",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitRange",
                table: "AiTaskDefinitions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContextPathsJson",
                table: "AiTaskDefinitions");

            migrationBuilder.DropColumn(
                name: "GitRange",
                table: "AiTaskDefinitions");
        }
    }
}
