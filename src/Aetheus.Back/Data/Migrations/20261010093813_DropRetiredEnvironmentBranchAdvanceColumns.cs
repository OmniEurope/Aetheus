using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <summary>
    /// Recette R2-001, contract step 2 of 2: drops Environments.AdvanceBranchOnDeploy and
    /// Environments.AdvanceBranchName. Step 1 (20261002163217_UnmapRetiredEnvironmentBranchAdvance)
    /// stopped mapping them and shipped in v1.3.6 and v1.3.9, so the previous colour (V-1) neither
    /// reads nor writes them any more. Named as a reviewed contract drop in ExpandContractMigrationAuditTests.
    /// </summary>
    public partial class DropRetiredEnvironmentBranchAdvanceColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: contract drop (R2-001, user authorisation 2026-10-02).
            // V-1 (v1.3.9) carries 20261002163217_UnmapRetiredEnvironmentBranchAdvance and maps neither
            // column, so dropping them cannot break the colour still serving nor the V/V-1 rollback gate.
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
