using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDockerContainerRisks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DockerRiskAlertedJson",
                table: "Servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AetheusRole",
                table: "DockerContainers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DangerousCapabilities",
                table: "DockerContainers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Risks",
                table: "DockerContainers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SecurityInspected",
                table: "DockerContainers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DockerRiskAlertedJson",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "AetheusRole",
                table: "DockerContainers");

            migrationBuilder.DropColumn(
                name: "DangerousCapabilities",
                table: "DockerContainers");

            migrationBuilder.DropColumn(
                name: "Risks",
                table: "DockerContainers");

            migrationBuilder.DropColumn(
                name: "SecurityInspected",
                table: "DockerContainers");
        }
    }
}
