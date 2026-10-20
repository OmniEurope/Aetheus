using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <summary>Keep unmapped environment branch-advance columns for the preceding release, with
    /// false and nullable defaults supporting inserts that omit them. Remove the columns only in a
    /// later compatible contract step documented in docs/contracts/deployment.md.</summary>
    public partial class UnmapRetiredEnvironmentBranchAdvance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
