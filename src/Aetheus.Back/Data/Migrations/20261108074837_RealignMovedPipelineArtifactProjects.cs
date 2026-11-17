using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class RealignMovedPipelineArtifactProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: BACK-002 (user decision: artifacts follow a moved pipeline),
            // data-only, no row and no column removed. A pipeline moved before this release left its artifacts
            // stamped with the project it came from, so that project still listed them, counted them in its quota
            // and applied its retention, while their download was already authorized against the new owner. Each
            // such artifact takes the project its pipeline resolves to today (direct ProjectId, else its
            // environment's project, else its project server's project). Only an artifact that names a
            // DIFFERENT project is touched: one whose column is null stays null, and one whose pipeline resolves
            // to no project is left alone. The previous colour is unaffected: it reads the same column.
            migrationBuilder.Sql(
                """
                UPDATE "PipelineArtifacts" AS a
                SET "ProjectId" = owner.project_id
                FROM (
                    SELECT p."Id" AS pipeline_id,
                           COALESCE(p."ProjectId", e."ProjectId", ps."ProjectId") AS project_id
                    FROM "Pipelines" AS p
                    LEFT JOIN "Environments" AS e ON e."Id" = p."EnvironmentId"
                    LEFT JOIN "ProjectServers" AS ps ON ps."Id" = p."ProjectServerId"
                ) AS owner
                WHERE a."PipelineId" = owner.pipeline_id
                  AND owner.project_id IS NOT NULL
                  AND a."ProjectId" IS NOT NULL
                  AND a."ProjectId" <> owner.project_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: the project an artifact was stamped with before its pipeline moved is not kept.
            // Rolling back leaves the realigned artifacts on their pipeline's current project, which the
            // previous release reads as it reads any other artifact.
        }
    }
}
