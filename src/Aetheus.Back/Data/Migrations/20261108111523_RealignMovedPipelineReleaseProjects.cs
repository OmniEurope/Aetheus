using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class RealignMovedPipelineReleaseProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: BACK-002 follow-up (the releases follow a moved pipeline like
            // its artifacts), data-only, no row and no column removed. A pipeline moved before this release left
            // the releases it published in the project they came from, so that project still listed them and
            // applied its retention and permissions while the pipeline already belonged to another one. A release
            // names its publishing pipeline through CreatedByPipelineRunId (the run that first published it;
            // PipelineRunId is not used, a deployment overwrites it with another pipeline's run), and takes the
            // project that pipeline resolves to today (direct ProjectId, else its environment's project, else its
            // project server's project). Only a release naming a DIFFERENT project is touched; one without a
            // creating run, or whose pipeline resolves to no project, is left alone. The unique indexes of
            // Releases are respected, conservatively: a release whose version already exists in the destination,
            // that is not the lowest id of its destination and version, that is live (Status 6, Deployed) while
            // the destination already has a live release, or that is not the lowest-id live candidate of its
            // destination, stays where it is.
            migrationBuilder.Sql(
                """
                WITH candidate AS (
                    SELECT rel."Id" AS release_id,
                           rel."Version" AS version,
                           rel."Status" AS status,
                           COALESCE(p."ProjectId", e."ProjectId", ps."ProjectId") AS project_id
                    FROM "Releases" AS rel
                    JOIN "PipelineRuns" AS run ON run."Id" = rel."CreatedByPipelineRunId"
                    JOIN "Pipelines" AS p ON p."Id" = run."PipelineId"
                    LEFT JOIN "Environments" AS e ON e."Id" = p."EnvironmentId"
                    LEFT JOIN "ProjectServers" AS ps ON ps."Id" = p."ProjectServerId"
                    WHERE COALESCE(p."ProjectId", e."ProjectId", ps."ProjectId") IS NOT NULL
                      AND COALESCE(p."ProjectId", e."ProjectId", ps."ProjectId") <> rel."ProjectId"
                ),
                eligible AS (
                    SELECT c.*
                    FROM candidate AS c
                    WHERE NOT EXISTS (
                              SELECT 1 FROM "Releases" AS x
                              WHERE x."ProjectId" = c.project_id AND x."Version" = c.version)
                      AND NOT (c.status = 6 AND EXISTS (
                              SELECT 1 FROM "Releases" AS x
                              WHERE x."ProjectId" = c.project_id AND x."Status" = 6))
                ),
                ranked AS (
                    SELECT el.release_id,
                           el.project_id,
                           el.status,
                           ROW_NUMBER() OVER (PARTITION BY el.project_id, el.version ORDER BY el.release_id) AS version_rank,
                           ROW_NUMBER() OVER (PARTITION BY el.project_id, (el.status = 6) ORDER BY el.release_id) AS live_rank
                    FROM eligible AS el
                )
                UPDATE "Releases" AS r
                SET "ProjectId" = ranked.project_id
                FROM ranked
                WHERE r."Id" = ranked.release_id
                  AND ranked.version_rank = 1
                  AND (ranked.status <> 6 OR ranked.live_rank = 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: the project a release was stamped with before its pipeline moved is not kept.
            // Rolling back leaves the realigned releases on their pipeline's current project, which the
            // previous release reads as it reads any other release.
        }
    }
}
