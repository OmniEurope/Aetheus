using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class NumberPipelineRunsPerPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: R-8, additive backfill, no row and no column removed. Every run
            // older than the per-pipeline counter still carries 0 (AddPipelineBuildNumber seeded the counter from
            // the run count, so numbers 1 to that count were left to them). Those runs take the lowest free numbers
            // of their pipeline, oldest first. A number already held is never changed: application versions were
            // built from it. A second run on the same number, which the counter cannot produce but nothing has
            // refused until now, is renumbered the same way so the index below cannot fail the deployment.
            // The previous colour is unaffected: it reads the column and allocates from the counter.
            migrationBuilder.Sql(
                """
                WITH occurrences AS (
                    SELECT "Id", "PipelineId", "StartedAt", "BuildNumber",
                           ROW_NUMBER() OVER (PARTITION BY "PipelineId", "BuildNumber" ORDER BY "StartedAt", "Id") AS occurrence
                    FROM "PipelineRuns"
                ),
                to_number AS (
                    SELECT "Id", "PipelineId",
                           ROW_NUMBER() OVER (PARTITION BY "PipelineId" ORDER BY "StartedAt", "Id") AS position
                    FROM occurrences
                    WHERE "BuildNumber" <= 0 OR occurrence > 1
                ),
                sizes AS (
                    SELECT "PipelineId", COUNT(*) AS total
                    FROM "PipelineRuns"
                    WHERE "PipelineId" IN (SELECT "PipelineId" FROM to_number)
                    GROUP BY "PipelineId"
                ),
                free_numbers AS (
                    SELECT sizes."PipelineId", candidate,
                           ROW_NUMBER() OVER (PARTITION BY sizes."PipelineId" ORDER BY candidate) AS position
                    FROM sizes
                    CROSS JOIN LATERAL generate_series(1, sizes.total) AS candidate
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "PipelineRuns" AS held
                        WHERE held."PipelineId" = sizes."PipelineId" AND held."BuildNumber" = candidate)
                )
                UPDATE "PipelineRuns" AS run
                SET "BuildNumber" = free_numbers.candidate
                FROM to_number
                JOIN free_numbers ON free_numbers."PipelineId" = to_number."PipelineId"
                    AND free_numbers.position = to_number.position
                WHERE run."Id" = to_number."Id";
                """);

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: R-8, additive. The counter only moves forward, so the next run of
            // a pipeline whose runs now reach past it (a demo pipeline seeded before this version) continues after them.
            migrationBuilder.Sql(
                """
                UPDATE "Pipelines" AS pipeline
                SET "BuildCounter" = highest.number
                FROM (
                    SELECT "PipelineId", MAX("BuildNumber") AS number
                    FROM "PipelineRuns"
                    GROUP BY "PipelineId"
                ) AS highest
                WHERE highest."PipelineId" = pipeline."Id" AND pipeline."BuildCounter" < highest.number;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_PipelineId_BuildNumber",
                table: "PipelineRuns",
                columns: new[] { "PipelineId", "BuildNumber" },
                unique: true,
                filter: "\"BuildNumber\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The numbers stay: a version that does not know them reads them as it reads any build number.
            migrationBuilder.DropIndex(
                name: "IX_PipelineRuns_PipelineId_BuildNumber",
                table: "PipelineRuns");
        }
    }
}
