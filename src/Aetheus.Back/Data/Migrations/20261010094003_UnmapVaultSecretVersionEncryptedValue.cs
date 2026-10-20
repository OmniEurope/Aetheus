using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <summary>Keep the unmapped EncryptedValue column with an empty-string default so both the
    /// preceding release, which writes it explicitly, and this release, which omits it, can insert
    /// rows. Column removal is a later contract step documented in
    /// docs/contracts/deployment.md.</summary>
    public partial class UnmapVaultSecretVersionEncryptedValue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: additive empty-string default on the retained
            // NOT NULL text column. It changes no stored row and supports inserts omitting the
            // unmapped field.
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
