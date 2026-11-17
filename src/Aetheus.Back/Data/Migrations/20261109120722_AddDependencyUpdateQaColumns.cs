using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <summary>
    /// PLAN-007: an automatic dependency update runs the project's QA after its CI, never a candidate. The two QA columns replace the
    /// candidate ones. The migration is expand-only (the expand/contract guard forbids a rename in a future migration): the old
    /// nullable CandidatePipelineId and CandidatePipelineRunId columns are no longer mapped and stay in place, without meaning, until a
    /// later contract migration drops them once the previous release no longer maps them.
    /// </summary>
    public partial class AddDependencyUpdateQaColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QaPipelineId",
                table: "DependencyUpdates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QaPipelineRunId",
                table: "DependencyUpdates",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QaPipelineId",
                table: "DependencyUpdates");

            migrationBuilder.DropColumn(
                name: "QaPipelineRunId",
                table: "DependencyUpdates");
        }
    }
}
