using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <summary>
    /// Recette R-287, contract step 1 of 2: the model stops mapping VaultSecretVersions.EncryptedValue,
    /// the database keeps the column. The previous colour (V-1, v1.3.9) still maps it NOT NULL and inserts
    /// an empty string; this version omits it from its inserts, so the column receives a database default
    /// of '' for both colours to work against the shared schema during blue-green. Step 2, a later release
    /// whose V-1 is this one, drops the column (docs/contracts/deployment.md, pending contract steps).
    /// </summary>
    public partial class UnmapVaultSecretVersionEncryptedValue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: additive default only (recette R-287). The column stays
            // NOT NULL text; adding DEFAULT '' changes no row and nothing the previous colour relies on (it
            // always writes the column explicitly), and lets this version's inserts omit the unmapped column.
            migrationBuilder.Sql("ALTER TABLE \"VaultSecretVersions\" ALTER COLUMN \"EncryptedValue\" SET DEFAULT '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: rollback of the default above only (user authorisation
            // 2026-10-10). The blue-green gate inspects every Sql(...) call, Down() included; without this
            // marker deploy-prod run 2679 refused the migration.
            migrationBuilder.Sql("ALTER TABLE \"VaultSecretVersions\" ALTER COLUMN \"EncryptedValue\" DROP DEFAULT;");
        }
    }
}
