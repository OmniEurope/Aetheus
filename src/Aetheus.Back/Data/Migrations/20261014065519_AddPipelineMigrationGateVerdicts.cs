using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineMigrationGateVerdicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PipelineMigrationGateVerdicts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PipelineRunId = table.Column<int>(type: "integer", nullable: false),
                    StageName = table.Column<string>(type: "text", nullable: true),
                    StepName = table.Column<string>(type: "text", nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    PendingCount = table.Column<int>(type: "integer", nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MigrationsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineMigrationGateVerdicts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PipelineMigrationGateVerdicts_PipelineRuns_PipelineRunId",
                        column: x => x.PipelineRunId,
                        principalTable: "PipelineRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineMigrationGateVerdicts_PipelineRunId",
                table: "PipelineMigrationGateVerdicts",
                column: "PipelineRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PipelineMigrationGateVerdicts");
        }
    }
}
