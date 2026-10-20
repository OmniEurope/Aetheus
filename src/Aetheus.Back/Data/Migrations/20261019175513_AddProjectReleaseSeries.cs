using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectReleaseSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReleaseSeries",
                table: "PipelineRuns",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProjectReleaseNumberAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Series = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    AllocatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectReleaseNumberAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectReleaseNumberAllocations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectReleaseSeries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Series = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NextNumber = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectReleaseSeries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectReleaseSeries_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectReleaseNumberAllocations_ProjectId_Series_Number",
                table: "ProjectReleaseNumberAllocations",
                columns: new[] { "ProjectId", "Series", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectReleaseSeries_ProjectId_Series",
                table: "ProjectReleaseSeries",
                columns: new[] { "ProjectId", "Series" },
                unique: true);

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: insert and update backfill of the tables and the
            // column created above, no existing column is changed or dropped; the previous release
            // keeps reading and bumping Projects.ReleaseCounter, which stays. Until now one counter
            // was shared by every series. The product owner decided it becomes the counter of
            // series 1.3 (the series the counter was counting in; production is at 1.3.16), so 1.3
            // continues and every other series starts at 0. The same counter is kept for the empty
            // series, which a project whose code declares no VersionPrefix counts in, so such a
            // project continues too. Numbers 0..counter-1 are recorded as handed out, and the runs
            // that took one are stamped with series 1.3.
            migrationBuilder.Sql(
                """
                INSERT INTO "ProjectReleaseSeries" ("ProjectId", "Series", "NextNumber")
                SELECT p."Id", s."Series", p."ReleaseCounter"
                FROM "Projects" p
                CROSS JOIN (VALUES ('1.3'), ('')) AS s("Series")
                WHERE p."ReleaseCounter" > 0;

                INSERT INTO "ProjectReleaseNumberAllocations" ("ProjectId", "Series", "Number", "AllocatedAt")
                SELECT p."Id", s."Series", n.number, NOW()
                FROM "Projects" p
                CROSS JOIN (VALUES ('1.3'), ('')) AS s("Series")
                CROSS JOIN LATERAL generate_series(0, p."ReleaseCounter" - 1) AS n(number)
                WHERE p."ReleaseCounter" > 0;

                UPDATE "PipelineRuns" SET "ReleaseSeries" = '1.3' WHERE "ReleaseNumber" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectReleaseNumberAllocations");

            migrationBuilder.DropTable(
                name: "ProjectReleaseSeries");

            migrationBuilder.DropColumn(
                name: "ReleaseSeries",
                table: "PipelineRuns");
        }
    }
}
