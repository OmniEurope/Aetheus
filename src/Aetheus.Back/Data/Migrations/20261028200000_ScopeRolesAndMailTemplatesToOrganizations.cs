using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeRolesAndMailTemplatesToOrganizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OrganizationId",
                table: "Roles",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrganizationMailTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationMailTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizationMailTemplates_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Roles_OrganizationId",
                table: "Roles",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMailTemplates_OrganizationId_Kind_Language",
                table: "OrganizationMailTemplates",
                columns: new[] { "OrganizationId", "Kind", "Language" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Roles_Organizations_OrganizationId",
                table: "Roles",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: additive backfill of the two keys this migration's release
            // adds to the catalogue (Administration for organization admins, owner decision 2026-10-28), in the
            // style of 20261028180000_AddRolePermissionGrants: the roles named 'Organization Admin' and 'Admin'
            // receive org.roles.manage and org.mailtemplates.manage role-wide. Nothing is updated nor deleted;
            // a fresh install seeds the same grants from the catalogue (BuiltInRoleGrants).
            migrationBuilder.Sql(
                """
                INSERT INTO "RolePermissionGrants" ("RoleId", "PermissionKey", "ResourceId")
                SELECT r."Id", k.permission_key, NULL
                FROM "Roles" r
                CROSS JOIN (VALUES ('org.roles.manage'), ('org.mailtemplates.manage')) AS k(permission_key)
                WHERE r."Name" IN ('Organization Admin', 'Admin')
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: rollback of the Up() backfill above only, no schema change.
            // It deletes just the RolePermissionGrants rows holding the two keys this release introduced, which
            // the previous release's catalogue does not know and never reads; the table and every other grant
            // stay, so the previous release keeps running against the shared schema.
            // The previous catalogue does not know the two keys: their grants leave with them.
            migrationBuilder.Sql(
                """
                DELETE FROM "RolePermissionGrants"
                WHERE "PermissionKey" IN ('org.roles.manage', 'org.mailtemplates.manage');
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Roles_Organizations_OrganizationId",
                table: "Roles");

            migrationBuilder.DropTable(
                name: "OrganizationMailTemplates");

            migrationBuilder.DropIndex(
                name: "IX_Roles_OrganizationId",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Roles");
        }
    }
}
