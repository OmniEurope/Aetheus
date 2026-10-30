using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSyntheticAiReviewFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: data-only, no schema change, the previous release
            // reads the same tables unchanged. The AI review used to publish one synthetic finding
            // ("[FAKE] Synthetic placeholder", rule ai-review-placeholder, a rule nothing else uses)
            // when the model reported nothing; the user decided (R-33) that no fake finding may stay.
            // Deleting the findings of that rule removes their occurrences and decisions through the
            // cascading foreign keys.
            migrationBuilder.Sql(
                """
                DELETE FROM "AnalysisFindings" WHERE "RuleId" = 'ai-review-placeholder';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deleted synthetic findings are not restored: they were fake.
        }
    }
}
