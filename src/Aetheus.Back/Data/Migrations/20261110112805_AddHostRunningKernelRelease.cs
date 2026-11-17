using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHostRunningKernelRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RunningKernelRelease",
                table: "ServerHostInventories",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: data-only, resets the resume point of the Ubuntu OSV feed, no column
            // removed. The Ubuntu advisories were stored without their severity ("severity":[{"type":"Ubuntu","score":"high"}]
            // was not read, so they all counted High and unpublished); the severity is not kept in raw form, only the source
            // record has it, so the advisories must be read again. Clearing the load marker, the dump ETag and the resume
            // point makes the next scheduled pass (or an administrator's pass) reload the whole Ubuntu dump, which replaces
            // every stored advisory (a record no older than the stored one is applied) with its corrected severity; the
            // findings of the host systems read that severity live and are matched again at the end of the same pass. The
            // other feeds are untouched; a feed never loaded matches no row.
            migrationBuilder.Sql(
                """
                UPDATE "VulnerabilityFeedStates"
                SET "LoadCompletedAt" = NULL,
                    "DumpETag" = NULL,
                    "ETag" = NULL,
                    "LoadedEntries" = 0,
                    "LoadMaxModifiedAt" = NULL
                WHERE "Source" = 'osv:Ubuntu';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RunningKernelRelease",
                table: "ServerHostInventories");
        }
    }
}
