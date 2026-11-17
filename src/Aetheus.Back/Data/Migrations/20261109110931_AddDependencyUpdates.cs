using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDependencyUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DependencyUpdatesEnabled",
                table: "Projects",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "DependencyUpdates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<int>(type: "integer", nullable: false),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    GitInternalRepoId = table.Column<int>(type: "integer", nullable: false),
                    VulnerabilityMatchId = table.Column<long>(type: "bigint", nullable: true),
                    Ecosystem = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PackageName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FromVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TargetVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Advisory = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    License = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    LicenseOfTargetVersion = table.Column<bool>(type: "boolean", nullable: false),
                    Branch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CiPipelineId = table.Column<int>(type: "integer", nullable: true),
                    CiPipelineRunId = table.Column<int>(type: "integer", nullable: true),
                    CandidatePipelineId = table.Column<int>(type: "integer", nullable: true),
                    CandidatePipelineRunId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DependencyUpdates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DependencyUpdates_GitInternalRepos_GitInternalRepoId",
                        column: x => x.GitInternalRepoId,
                        principalTable: "GitInternalRepos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DependencyUpdates_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DependencyUpdates_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DependencyUpdates_VulnerabilityMatches_VulnerabilityMatchId",
                        column: x => x.VulnerabilityMatchId,
                        principalTable: "VulnerabilityMatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DependencyUpdates_GitInternalRepoId_Ecosystem_PackageName_T~",
                table: "DependencyUpdates",
                columns: new[] { "GitInternalRepoId", "Ecosystem", "PackageName", "TargetVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DependencyUpdates_OrganizationId",
                table: "DependencyUpdates",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_DependencyUpdates_ProjectId",
                table: "DependencyUpdates",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_DependencyUpdates_VulnerabilityMatchId",
                table: "DependencyUpdates",
                column: "VulnerabilityMatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DependencyUpdates");

            migrationBuilder.DropColumn(
                name: "DependencyUpdatesEnabled",
                table: "Projects");
        }
    }
}
