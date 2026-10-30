using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeRoleAssignmentsToOrganizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserOrganizationRoles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    OrganizationId = table.Column<int>(type: "integer", nullable: false),
                    RoleId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserOrganizationRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserOrganizationRoles_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserOrganizationRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserOrganizationRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserOrganizationRoles_OrganizationId_RoleId",
                table: "UserOrganizationRoles",
                columns: new[] { "OrganizationId", "RoleId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserOrganizationRoles_RoleId",
                table: "UserOrganizationRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserOrganizationRoles_UserId_OrganizationId_RoleId",
                table: "UserOrganizationRoles",
                columns: new[] { "UserId", "OrganizationId", "RoleId" },
                unique: true);

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: backfill of the table created above plus the built-in
            // roles it relies on (ADR-059, R-45, R-46, R-47). Nothing is dropped or deleted and UserRoles
            // is untouched, so the previous release still reads it after a rollback; the only update
            // (step 4) raises a membership level, which that release reads as it did before.
            // 1. The built-in roles, created only where missing (an older install may lack Organization
            //    Admin, and Reader or Contributor if they were deleted), with their wildcard permissions
            //    (ResourceType 0..10; Permission Read = 0, Write = 1, Admin = 2). A role that already has
            //    permissions keeps them.
            // 2. Every existing member gets the role its membership level gives: Member = Reader,
            //    Maintainer = Contributor, Owner = Organization Admin (OrganizationRole 0, 1, 2).
            // 3. Every role a user held site-wide, other than Admin, becomes the same role for each
            //    organization the user is a member of. The UserRoles rows stay; the new code honours a
            //    site-wide row for Admin only, so a user without membership is left with no right.
            // 4. Each membership level is raised to the highest built-in role held for that organization
            //    (Contributor = Maintainer, Organization Admin = Owner), so the level never lags the roles.
            migrationBuilder.Sql(
                """
                INSERT INTO "Roles" ("Name", "Description") VALUES
                    ('Reader', 'Read access to the resources of its organization'),
                    ('Contributor', 'Read and write access to the resources of its organization'),
                    ('Organization Admin', 'Full access to the resources of its organization, and management of its members')
                ON CONFLICT ("Name") DO NOTHING;

                INSERT INTO "ResourcePermissions" ("RoleId", "ResourceType", "ResourceId", "Permission")
                SELECT r."Id", t.resource_type, NULL,
                       CASE r."Name" WHEN 'Reader' THEN 0 WHEN 'Contributor' THEN 1 ELSE 2 END
                FROM "Roles" r
                CROSS JOIN generate_series(0, 10) AS t(resource_type)
                WHERE r."Name" IN ('Reader', 'Contributor', 'Organization Admin')
                  AND NOT EXISTS (SELECT 1 FROM "ResourcePermissions" p WHERE p."RoleId" = r."Id");

                INSERT INTO "UserOrganizationRoles" ("UserId", "OrganizationId", "RoleId", "CreatedAt")
                SELECT m."UserId", m."OrganizationId", r."Id", NOW()
                FROM "OrganizationMembers" m
                JOIN "Roles" r ON r."Name" = CASE m."Role"
                    WHEN 2 THEN 'Organization Admin' WHEN 1 THEN 'Contributor' ELSE 'Reader' END
                ON CONFLICT ("UserId", "OrganizationId", "RoleId") DO NOTHING;

                INSERT INTO "UserOrganizationRoles" ("UserId", "OrganizationId", "RoleId", "CreatedAt")
                SELECT ur."UserId", m."OrganizationId", ur."RoleId", NOW()
                FROM "UserRoles" ur
                JOIN "Roles" r ON r."Id" = ur."RoleId"
                JOIN "OrganizationMembers" m ON m."UserId" = ur."UserId"
                WHERE r."Name" <> 'Admin'
                ON CONFLICT ("UserId", "OrganizationId", "RoleId") DO NOTHING;

                UPDATE "OrganizationMembers" m
                SET "Role" = held.level
                FROM (
                    SELECT a."UserId", a."OrganizationId",
                           MAX(CASE r."Name" WHEN 'Organization Admin' THEN 2 WHEN 'Contributor' THEN 1 ELSE 0 END) AS level
                    FROM "UserOrganizationRoles" a
                    JOIN "Roles" r ON r."Id" = a."RoleId"
                    GROUP BY a."UserId", a."OrganizationId"
                ) held
                WHERE m."UserId" = held."UserId"
                  AND m."OrganizationId" = held."OrganizationId"
                  AND m."Role" < held.level;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The built-in roles step 1 may have created stay: they are ordinary roles to the previous
            // release, and removing them could drop permissions an administrator has edited since.
            migrationBuilder.DropTable(
                name: "UserOrganizationRoles");
        }
    }
}
