using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <summary>Backfill project-creator subscriptions without changing schema; new projects
    /// subscribe their creators through ProjectService.</summary>
    public partial class SubscribeProjectCreatorsToTheirProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: insert-only backfill of an existing table, no schema
            // change; the previous colour already reads ProjectSubscriptions and simply sees more rows.
            // A project has no owner column: its creator is the user named by its first "Created" audit
            // entry (AuditService records the signed-in username). A project whose entry is missing, or
            // names "system" or a user that no longer exists or is inactive, has no identifiable owner
            // and gets no row. ON CONFLICT keeps an existing subscription and makes a re-run add nothing.
            migrationBuilder.Sql(
                "INSERT INTO \"ProjectSubscriptions\" (\"UserId\", \"ProjectId\", \"CreatedAt\") " +
                "SELECT DISTINCT ON (p.\"Id\") u.\"Id\", p.\"Id\", now() " +
                "FROM \"Projects\" p " +
                "JOIN \"AuditLogs\" a ON a.\"EntityType\" = 'Project' AND a.\"Action\" = 'Created' AND a.\"EntityId\" = p.\"Id\" " +
                "JOIN \"Users\" u ON u.\"Username\" = a.\"Username\" AND u.\"IsActive\" " +
                "ORDER BY p.\"Id\", a.\"Id\" " +
                "ON CONFLICT (\"UserId\", \"ProjectId\") DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing: a backfilled row cannot be told from one the user made by following the project,
            // and removing a user's subscription is not the inverse of adding a missing one.
        }
    }
}
