using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class RequireUniqueUserEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PLAN-003 P3-2: the e-mail becomes the sign-in identifier. Stored e-mails take the form every
            // write now gives them (trimmed, lower case, a blank one is no e-mail). Accounts that then share
            // an address cannot both sign in by it, and choosing which one keeps it is a person's decision:
            // the migration stops, naming the accounts (ids and usernames, never the addresses), and the
            // transaction leaves the database as it was. No address is invented for an account without one.
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: data-only normalization plus a refusal, no schema change;
            // the previous release reads the lower-case addresses as before and only an exact duplicate it
            // would now write is refused by the new unique index.
            migrationBuilder.Sql("""
                UPDATE "Users"
                SET "Email" = NULLIF(lower(btrim("Email")), '')
                WHERE "Email" IS DISTINCT FROM NULLIF(lower(btrim("Email")), '');

                DO $$
                DECLARE
                    duplicate_accounts text;
                BEGIN
                    SELECT string_agg(accounts, '; ' ORDER BY accounts) INTO duplicate_accounts
                    FROM (
                        SELECT string_agg("Id"::text || ' (' || "Username" || ')', ', ' ORDER BY "Id") AS accounts
                        FROM "Users"
                        WHERE "Email" IS NOT NULL
                        GROUP BY "Email"
                        HAVING count(*) > 1
                    ) duplicates;
                    IF duplicate_accounts IS NOT NULL THEN
                        RAISE EXCEPTION 'Sign-in e-mail migration blocked: these accounts share an e-mail address (case-insensitive), give each one its own address or remove the extra account first: %.', duplicate_accounts;
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                table: "Users");
        }
    }
}
