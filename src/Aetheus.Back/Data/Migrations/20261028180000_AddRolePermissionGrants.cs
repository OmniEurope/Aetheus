using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRolePermissionGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RolePermissionGrants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<int>(type: "integer", nullable: false),
                    PermissionKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ResourceId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissionGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RolePermissionGrants_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissionGrants_RoleId_PermissionKey_ResourceId",
                table: "RolePermissionGrants",
                columns: new[] { "RoleId", "PermissionKey", "ResourceId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: backfill of the table created above (R-11). Nothing is
            // updated, dropped or deleted: ResourcePermissions keeps carrying the levels, so the previous
            // release reads the same rights after a rollback (Down drops this table only).
            // 1. Every role, built-in or custom, receives each action key on the scope where it held the
            //    level that allowed the action before (key, ResourceType, that level; Permission Read = 0,
            //    Write = 1, Admin = 2 and "at least" as the level checks compare): a role-wide level gives
            //    the role-wide key, a level on one resource gives the key on that resource. No role gains or
            //    loses an action, with the exceptions the owner decided for the built-in roles only: Reader
            //    and Contributor do not receive server.docker.container.env.view, and Contributor does not
            //    receive server.firewall.manage nor server.system.upgrade (Organization Admin's now).
            //    The keys and levels are written out: a migration never reads the live catalogue.
            // 2. org.members.manage replaces the "Organization Admin" role-name check of member management:
            //    the roles of that name (and the site Admin, which bypasses it anyway) hold it role-wide.
            migrationBuilder.Sql(
                """
                INSERT INTO "RolePermissionGrants" ("RoleId", "PermissionKey", "ResourceId")
                SELECT DISTINCT p."RoleId", k.permission_key, p."ResourceId"
                FROM "ResourcePermissions" p
                JOIN "Roles" r ON r."Id" = p."RoleId"
                JOIN (VALUES
                    ('pipeline.run', 2, 1),
                    ('pipeline.approval.decide', 2, 1),
                    ('release.rollback', 5, 1),
                    ('release.status.restore', 5, 1),
                    ('vault.secret.reveal', 1, 1),
                    ('server.services.control', 3, 1),
                    ('server.firewall.manage', 3, 1),
                    ('server.system.upgrade', 3, 1),
                    ('server.ports.reserve', 3, 1),
                    ('server.docker.container.env.view', 3, 0),
                    ('server.mailbox.read', 3, 0),
                    ('server.mailbox.manage', 3, 1)
                ) AS k(permission_key, resource_type, legacy_level)
                    ON k.resource_type = p."ResourceType" AND p."Permission" >= k.legacy_level
                WHERE NOT (k.permission_key = 'server.docker.container.env.view'
                           AND r."Name" IN ('Reader', 'Contributor'))
                  AND NOT (k.permission_key IN ('server.firewall.manage', 'server.system.upgrade')
                           AND r."Name" = 'Contributor')
                ON CONFLICT DO NOTHING;

                INSERT INTO "RolePermissionGrants" ("RoleId", "PermissionKey", "ResourceId")
                SELECT r."Id", 'org.members.manage', NULL
                FROM "Roles" r
                WHERE r."Name" IN ('Organization Admin', 'Admin')
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RolePermissionGrants");
        }
    }
}
