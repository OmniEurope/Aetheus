using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeAgentPoolsToOrganizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OrganizationId",
                table: "AgentPools",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: additive backfill of the new owner column (R-12, decision 7 of
            // 2026-10-28: every resource belongs to an organization), before the foreign key is added. Deterministic
            // rule, nothing is deleted and no other column is touched:
            // 1. a pool holding servers goes to the organization owning most of them (retired servers included,
            //    their links are kept); a tie goes to the smallest organization id;
            // 2. a pool without servers goes to the default organization (slug 'aetheus'), or, without one, to the
            //    organization with the smallest id.
            // A link to a server of another organization stays in AgentPoolServers: the API no longer shows it and
            // the pipeline resolver no longer uses it; the next edit of the pool drops it.
            migrationBuilder.Sql(
                """
                UPDATE "AgentPools" AS pool
                SET "OrganizationId" = ranked."OrganizationId"
                FROM (
                    SELECT link."AgentPoolId", server."OrganizationId",
                           ROW_NUMBER() OVER (PARTITION BY link."AgentPoolId"
                                              ORDER BY COUNT(*) DESC, server."OrganizationId") AS position
                    FROM "AgentPoolServers" AS link
                    JOIN "Servers" AS server ON server."Id" = link."ServerId"
                    GROUP BY link."AgentPoolId", server."OrganizationId"
                ) AS ranked
                WHERE ranked."AgentPoolId" = pool."Id" AND ranked.position = 1 AND pool."OrganizationId" = 0;

                UPDATE "AgentPools"
                SET "OrganizationId" = (
                    SELECT organization."Id" FROM "Organizations" AS organization
                    ORDER BY (organization."Slug" = 'aetheus') DESC, organization."Id"
                    LIMIT 1)
                WHERE "OrganizationId" = 0;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AgentPools_OrganizationId",
                table: "AgentPools",
                column: "OrganizationId");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentPools_Organizations_OrganizationId",
                table: "AgentPools",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentPools_Organizations_OrganizationId",
                table: "AgentPools");

            migrationBuilder.DropIndex(
                name: "IX_AgentPools_OrganizationId",
                table: "AgentPools");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "AgentPools");
        }
    }
}
