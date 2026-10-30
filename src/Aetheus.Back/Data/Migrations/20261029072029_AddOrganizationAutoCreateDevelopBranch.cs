using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationAutoCreateDevelopBranch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoCreateDevelopBranch",
                table: "Organizations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // R-76 (owner decision 2026-10-28): the platform organization works on develop, so it starts with the setting on;
            // every other organization keeps the default (off).
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: data-only, on the column added just above; the previous release does not map
            // it, so it reads the same table unchanged. A fresh install gets the same result: AddOrganizationOwnership already created
            // "Aetheus" before this migration runs.
            migrationBuilder.Sql(
                """
                UPDATE "Organizations" SET "AutoCreateDevelopBranch" = TRUE WHERE "Slug" = 'aetheus';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoCreateDevelopBranch",
                table: "Organizations");
        }
    }
}
