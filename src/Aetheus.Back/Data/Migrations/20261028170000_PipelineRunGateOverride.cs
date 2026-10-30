using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class PipelineRunGateOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "GateOverrideAt",
                table: "PipelineRuns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GateOverrideBy",
                table: "PipelineRuns",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GateOverrideWarningsJson",
                table: "PipelineRuns",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GateOverrideAt",
                table: "PipelineRuns");

            migrationBuilder.DropColumn(
                name: "GateOverrideBy",
                table: "PipelineRuns");

            migrationBuilder.DropColumn(
                name: "GateOverrideWarningsJson",
                table: "PipelineRuns");
        }
    }
}
