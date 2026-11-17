using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <summary>
    /// R-6: an automatic dependency update whose lock file needs its package manager records the cve-lock-refresh pipeline, its run,
    /// the pipeline owner it runs as and the lock files to read back. Expand-only: four nullable columns and an index, nothing dropped.
    /// </summary>
    public partial class DependencyUpdateLockRefresh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LockFilesJson",
                table: "DependencyUpdates",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LockPipelineId",
                table: "DependencyUpdates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LockPipelineRunId",
                table: "DependencyUpdates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LockRefreshOwner",
                table: "DependencyUpdates",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DependencyUpdates_LockPipelineRunId",
                table: "DependencyUpdates",
                column: "LockPipelineRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DependencyUpdates_LockPipelineRunId",
                table: "DependencyUpdates");

            migrationBuilder.DropColumn(
                name: "LockFilesJson",
                table: "DependencyUpdates");

            migrationBuilder.DropColumn(
                name: "LockPipelineId",
                table: "DependencyUpdates");

            migrationBuilder.DropColumn(
                name: "LockPipelineRunId",
                table: "DependencyUpdates");

            migrationBuilder.DropColumn(
                name: "LockRefreshOwner",
                table: "DependencyUpdates");
        }
    }
}
