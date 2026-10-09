using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectDataRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AnalysisDetailRetentionDays",
                table: "Projects",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AppLogRetentionDays",
                table: "Projects",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DataRetentionProfile",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TelemetryAggregateRetentionMonths",
                table: "Projects",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TelemetryDetailRetentionDays",
                table: "Projects",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TelemetryExportIntervalMinutes",
                table: "Projects",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnalysisDetailRetentionDays",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AppLogRetentionDays",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DataRetentionProfile",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "TelemetryAggregateRetentionMonths",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "TelemetryDetailRetentionDays",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "TelemetryExportIntervalMinutes",
                table: "Projects");
        }
    }
}
