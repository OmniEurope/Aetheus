using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemLogExclusionRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SystemLogExclusionRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<int>(type: "integer", nullable: false),
                    Pattern = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemLogExclusionRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SystemLogExclusionRules_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SystemLogExclusionRules_OrganizationId_Pattern_Category",
                table: "SystemLogExclusionRules",
                columns: new[] { "OrganizationId", "Pattern", "Category" },
                unique: true);

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: additive backfill of the two keys this migration's release adds
            // to the catalogue (R-28), in the style of 20261028200000_ScopeRolesAndMailTemplatesToOrganizations:
            // the built-in roles named 'Reader', 'Contributor', 'Organization Admin' and 'Admin' receive
            // org.logrules.view (a Reader reads the exclusion rules), and 'Contributor', 'Organization Admin' and
            // 'Admin' receive org.logrules.edit (least privilege: a Contributor edits). Nothing is updated nor
            // deleted; a fresh install seeds the same grants from the catalogue (BuiltInRoleGrants).
            migrationBuilder.Sql(
                """
                INSERT INTO "RolePermissionGrants" ("RoleId", "PermissionKey", "ResourceId")
                SELECT r."Id", k.permission_key, NULL
                FROM "Roles" r
                JOIN (VALUES
                    ('org.logrules.view', 'Reader'), ('org.logrules.view', 'Contributor'),
                    ('org.logrules.view', 'Organization Admin'), ('org.logrules.view', 'Admin'),
                    ('org.logrules.edit', 'Contributor'), ('org.logrules.edit', 'Organization Admin'),
                    ('org.logrules.edit', 'Admin')
                ) AS k(permission_key, role_name) ON k.role_name = r."Name"
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: rollback of the Up() backfill above only, no schema change in
            // this statement. It deletes just the RolePermissionGrants rows holding the two keys this release
            // introduced, which the previous release's catalogue does not know and never reads.
            migrationBuilder.Sql(
                """
                DELETE FROM "RolePermissionGrants"
                WHERE "PermissionKey" IN ('org.logrules.view', 'org.logrules.edit');
                """);

            migrationBuilder.DropTable(
                name: "SystemLogExclusionRules");
        }
    }
}
