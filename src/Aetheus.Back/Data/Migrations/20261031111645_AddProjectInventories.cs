using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectInventories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectInventories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<int>(type: "integer", nullable: false),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    GitInternalRepoId = table.Column<int>(type: "integer", nullable: true),
                    Origin = table.Column<int>(type: "integer", nullable: false),
                    Ref = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Coverage = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SourceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ItemCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectInventories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectInventories_GitInternalRepos_GitInternalRepoId",
                        column: x => x.GitInternalRepoId,
                        principalTable: "GitInternalRepos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectInventories_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectInventories_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectInventoryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<int>(type: "integer", nullable: false),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    ProjectInventoryId = table.Column<int>(type: "integer", nullable: false),
                    Ecosystem = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PackageName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PackageUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    IsDirect = table.Column<bool>(type: "boolean", nullable: false),
                    SourcePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectInventoryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectInventoryItems_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectInventoryItems_ProjectInventories_ProjectInventoryId",
                        column: x => x.ProjectInventoryId,
                        principalTable: "ProjectInventories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectInventoryItems_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectInventories_GitInternalRepoId",
                table: "ProjectInventories",
                column: "GitInternalRepoId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectInventories_OrganizationId",
                table: "ProjectInventories",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectInventories_ProjectId_Origin_GitInternalRepoId",
                table: "ProjectInventories",
                columns: new[] { "ProjectId", "Origin", "GitInternalRepoId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectInventories_ReadAt",
                table: "ProjectInventories",
                column: "ReadAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectInventoryItems_Ecosystem_PackageName",
                table: "ProjectInventoryItems",
                columns: new[] { "Ecosystem", "PackageName" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectInventoryItems_OrganizationId",
                table: "ProjectInventoryItems",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectInventoryItems_ProjectId",
                table: "ProjectInventoryItems",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectInventoryItems_ProjectInventoryId_Ecosystem_PackageN~",
                table: "ProjectInventoryItems",
                columns: new[] { "ProjectInventoryId", "Ecosystem", "PackageName", "Version", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectInventoryItems");

            migrationBuilder.DropTable(
                name: "ProjectInventories");
        }
    }
}
