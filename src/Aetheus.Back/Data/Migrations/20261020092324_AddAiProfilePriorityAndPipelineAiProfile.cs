using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiProfilePriorityAndPipelineAiProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AiRunnerProfileId",
                table: "Pipelines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "AiRunnerProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 100);

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: backfill of the column added just above, no existing
            // column is changed or dropped. Existing profiles keep their current preference order: within
            // an organization they are numbered 10, 20, 30... by Id, so the oldest profile stays the
            // first choice and a gap is left to slot a new profile in between. A profile inserted by the
            // previous release meanwhile keeps the column default of 100.
            migrationBuilder.Sql(
                """
                UPDATE "AiRunnerProfiles" AS profile
                SET "Priority" = ranked."Rank" * 10
                FROM (
                    SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "OrganizationId" ORDER BY "Id") AS "Rank"
                    FROM "AiRunnerProfiles"
                ) AS ranked
                WHERE profile."Id" = ranked."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_AiRunnerProfileId",
                table: "Pipelines",
                column: "AiRunnerProfileId");

            migrationBuilder.AddForeignKey(
                name: "FK_Pipelines_AiRunnerProfiles_AiRunnerProfileId",
                table: "Pipelines",
                column: "AiRunnerProfileId",
                principalTable: "AiRunnerProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Pipelines_AiRunnerProfiles_AiRunnerProfileId",
                table: "Pipelines");

            migrationBuilder.DropIndex(
                name: "IX_Pipelines_AiRunnerProfileId",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "AiRunnerProfileId",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "AiRunnerProfiles");
        }
    }
}
