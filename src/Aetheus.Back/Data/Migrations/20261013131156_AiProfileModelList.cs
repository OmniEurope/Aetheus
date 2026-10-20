using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiProfileModelList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AvailableModelsFetchedAt",
                table: "AiRunnerProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AvailableModelsJson",
                table: "AiRunnerProfiles",
                type: "character varying(120000)",
                maxLength: 120000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModelListCheckedAt",
                table: "AiRunnerProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelListError",
                table: "AiRunnerProfiles",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvailableModelsFetchedAt",
                table: "AiRunnerProfiles");

            migrationBuilder.DropColumn(
                name: "AvailableModelsJson",
                table: "AiRunnerProfiles");

            migrationBuilder.DropColumn(
                name: "ModelListCheckedAt",
                table: "AiRunnerProfiles");

            migrationBuilder.DropColumn(
                name: "ModelListError",
                table: "AiRunnerProfiles");
        }
    }
}
