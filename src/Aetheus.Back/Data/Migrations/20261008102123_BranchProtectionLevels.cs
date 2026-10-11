using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <summary>
    /// Decision D13 (BACK-013), expand step: branch protection gets three levels (BranchProtectionLevel:
    /// 0 NotProtected, 1 ProtectedExceptPipelines, 2 StrictlyProtected). A rule that required a pull
    /// request becomes ProtectedExceptPipelines, the behaviour it had (direct pushes refused, the
    /// advance-branch step allowed), so the release pipelines keep advancing their branch; every other
    /// rule stays NotProtected, as before. RequirePullRequest stays mapped and is kept in step by the
    /// entity, because the previous colour (V-1) still reads it; the contract step drops it
    /// (docs/contracts/deployment.md, pending contract steps).
    /// </summary>
    public partial class BranchProtectionLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProtectionLevel",
                table: "BranchProtectionRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: data-only backfill of the column added just above, from a
            // column that stays.
            migrationBuilder.Sql(
                "UPDATE \"BranchProtectionRules\" SET \"ProtectionLevel\" = 1 WHERE \"RequirePullRequest\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProtectionLevel",
                table: "BranchProtectionRules");
        }
    }
}
