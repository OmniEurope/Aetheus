using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineExternalImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalImportCommit",
                table: "Pipelines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalImportPath",
                table: "Pipelines",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalImportSha256",
                table: "Pipelines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExternalImportedAt",
                table: "Pipelines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalImportedBy",
                table: "Pipelines",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalImportCommit",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "ExternalImportPath",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "ExternalImportSha256",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "ExternalImportedAt",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "ExternalImportedBy",
                table: "Pipelines");
        }
    }
}
