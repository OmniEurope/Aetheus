using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class PipelineApprovalScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: DROP NOT NULL widens accepted rows without
            // changing existing data, foreign keys or indexes. The preceding release hides
            // environment-less approvals through its required join, so cancel runs awaiting those
            // approvals before rollback.
            migrationBuilder.Sql("ALTER TABLE \"PipelineApprovals\" ALTER COLUMN \"EnvironmentId\" DROP NOT NULL;");

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "PipelineApprovals",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: backfill of the column added just above, no schema
            // change. Only a stage's own confirmation window ever stored TimeoutMinutes (an environment
            // approval leaves it null and uses the environment's delay), so those rows are the pipeline's
            // approvals (Scope 1). Without it, a run waiting on one across the upgrade would be asked
            // again for the same stage. The previous colour never reads Scope; a confirmation it raises
            // after this ran keeps Scope 0, so at worst that stage is asked once more, never less.
            migrationBuilder.Sql("UPDATE \"PipelineApprovals\" SET \"Scope\" = 1 WHERE \"TimeoutMinutes\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Scope",
                table: "PipelineApprovals");

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: Down only. An environment-less approval cannot survive
            // NOT NULL: it goes, with the decision it held.
            migrationBuilder.Sql("DELETE FROM \"PipelineApprovals\" WHERE \"EnvironmentId\" IS NULL;");
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: Down only, restores the constraint once no row violates it.
            migrationBuilder.Sql("ALTER TABLE \"PipelineApprovals\" ALTER COLUMN \"EnvironmentId\" SET NOT NULL;");
        }
    }
}
