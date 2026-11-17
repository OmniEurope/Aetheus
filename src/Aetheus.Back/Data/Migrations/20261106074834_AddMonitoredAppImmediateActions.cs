using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoredAppImmediateActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ProbeRequestResultAt",
                table: "MonitoredApps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProbeRequestedAt",
                table: "MonitoredApps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TelemetryExportDeliveredAt",
                table: "MonitoredApps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TelemetryExportReceivedAt",
                table: "MonitoredApps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TelemetryExportRequestedAt",
                table: "MonitoredApps",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProbeRequestResultAt",
                table: "MonitoredApps");

            migrationBuilder.DropColumn(
                name: "ProbeRequestedAt",
                table: "MonitoredApps");

            migrationBuilder.DropColumn(
                name: "TelemetryExportDeliveredAt",
                table: "MonitoredApps");

            migrationBuilder.DropColumn(
                name: "TelemetryExportReceivedAt",
                table: "MonitoredApps");

            migrationBuilder.DropColumn(
                name: "TelemetryExportRequestedAt",
                table: "MonitoredApps");
        }
    }
}
