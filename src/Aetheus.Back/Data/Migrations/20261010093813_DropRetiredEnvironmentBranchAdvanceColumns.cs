using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <summary>Drops the environment branch-advance columns after their mappings were removed in
    /// the supported preceding release. ExpandContractMigrationAuditTests records this reviewed
    /// contract drop.</summary>
    public partial class DropRetiredEnvironmentBranchAdvanceColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: contract drop; the supported preceding release
            // v1.3.9 maps neither column after 20261002163217_UnmapRetiredEnvironmentBranchAdvance,
            // preserving shared-schema rollback compatibility.
            migrationBuilder.Sql("ALTER TABLE \"Environments\" DROP COLUMN IF EXISTS \"AdvanceBranchOnDeploy\";");

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: second column of the same reviewed contract drop.
            migrationBuilder.Sql("ALTER TABLE \"Environments\" DROP COLUMN IF EXISTS \"AdvanceBranchName\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restores the shape added by 20260829111212_AddEnvironmentBranchAdvance; the values are gone.
            migrationBuilder.AddColumn<string>(
                name: "AdvanceBranchName",
                table: "Environments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AdvanceBranchOnDeploy",
                table: "Environments",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
