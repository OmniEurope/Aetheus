using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <summary>
    /// The seeded administrator of an install older than e-mail sign-in may have no address, and
    /// <see cref="RequireUserEmail"/> (shipped, so never edited) then refuses to migrate it. Ordered just before that
    /// migration on purpose: an install that has not run it yet gets the address first. Owner decision 2026-10-29:
    /// the single seeded administrator (<c>admin</c>) without an address receives the installation's default one
    /// (<see cref="SeededAdminEmail.Default"/>), the address it then signs in with. Any other account
    /// without an address still stops <see cref="RequireUserEmail"/>: an address is never invented for a person.
    /// On a database where <see cref="RequireUserEmail"/> already ran, every account has an address and this changes
    /// nothing.
    /// </summary>
    public partial class FillSeededAdminEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: data only, no schema change. It writes one address on the seeded
            // administrator when it has none (NULL or blank) and no other account holds the default address (case-
            // insensitive, so the unique e-mail index cannot fail); otherwise it does nothing. A colour still serving
            // reads the address like any other; nothing is removed. Idempotent: a re-run finds no account to fill.
            migrationBuilder.Sql($"""
                UPDATE "Users" AS seeded
                SET "Email" = '{SeededAdminEmail.Default}'
                WHERE seeded."Username" = '{SeededAdminEmail.SeededUsername}'
                  AND (seeded."Email" IS NULL OR btrim(seeded."Email") = '')
                  AND NOT EXISTS (
                      SELECT 1 FROM "Users" AS other
                      WHERE other."Id" <> seeded."Id"
                        AND lower(btrim(other."Email")) = lower('{SeededAdminEmail.Default}'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the address given is a valid one, and removing it would block RequireUserEmail again.
        }
    }
}
