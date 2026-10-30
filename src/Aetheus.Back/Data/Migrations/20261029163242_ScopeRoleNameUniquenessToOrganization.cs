using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeRoleNameUniquenessToOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // R-2: a role name is unique within its organization (NULLS NOT DISTINCT: among the site roles too).
            // Every existing name is unique site-wide (IX_Roles_Name), so the new index cannot fail on existing rows.
            migrationBuilder.CreateIndex(
                name: "IX_Roles_OrganizationId_Name",
                table: "Roles",
                columns: new[] { "OrganizationId", "Name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: the site-wide unique name index is relaxed to the per-organization
            // one created above. Safe for the version still running during a blue-green cutover: it checks a new name
            // against every role before writing (stricter than either index), so it never depends on IX_Roles_Name
            // to stay correct. IX_Roles_OrganizationId (the foreign key's index) is covered by the leading column of
            // the new index. No data is removed. Raw SQL with IF EXISTS so a re-run cannot fail the deploy. Rolling
            // back fails once two organizations share a role name: rename one first.
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Roles_Name\";");
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: the foreign key's own index on OrganizationId is redundant with the
            // leading column of IX_Roles_OrganizationId_Name; no constraint and no data go with it.
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Roles_OrganizationId\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Roles_OrganizationId_Name",
                table: "Roles");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_OrganizationId",
                table: "Roles",
                column: "OrganizationId");
        }
    }
}
