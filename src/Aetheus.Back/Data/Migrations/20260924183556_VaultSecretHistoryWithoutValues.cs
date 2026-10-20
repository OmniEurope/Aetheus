using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class VaultSecretHistoryWithoutValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChangedBy",
                table: "VaultSecretVersions",
                type: "text",
                nullable: true);

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: erase stored secret-history values that no
            // supported binary reads while retaining metadata and schema. This erasure is
            // irreversible; later column removal requires a separate contract migration.
            migrationBuilder.Sql("UPDATE \"VaultSecretVersions\" SET \"EncryptedValue\" = '' WHERE \"EncryptedValue\" <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChangedBy",
                table: "VaultSecretVersions");
        }
    }
}
