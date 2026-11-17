using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveConflictingPortClaimDuplicates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: BACK-008 follow-up, data-only, deletes only redundant rows of
            // ServerPortReservations, no column removed. Before the per-server advisory lock, a declaration and a
            // manual reservation (the unique index is keyed on Source, so it lets both exist) could both take the
            // same TCP port of a server for DIFFERENT owners, the one claim the conflict rule exists to forbid.
            // Group: (ServerId, Port) over the Declared (0) and Manual (1) rows whose protocol the code reads as
            // TCP (anything but 'udp', as PortObservationWriter.NormalizeProtocol); Observed rows (2) describe the
            // host and are never touched. Kept: the claim that came first, the order the code enforces (every
            // claim path refuses a port another owner already holds), by DeclaredAt (set once, at creation), then
            // Declared before Manual (the order the conflict report names its blocking holder), then lowest Id.
            // Deleted: every other row of the group held by a DIFFERENT owner than the kept one. A row of the
            // same owner is not a duplicate for the code ("a port this owner already holds is not a conflict")
            // and stays. No unique index is added: "one owner per port" is not expressible as one, and a
            // (ServerId, Port) index would refuse same-owner Declared and Manual rows the code still allows.
            migrationBuilder.Sql(
                """
                DELETE FROM "ServerPortReservations" AS loser
                USING (
                    SELECT c."Id" AS reservation_id,
                           c."OwnerKey" AS owner_key,
                           FIRST_VALUE(c."OwnerKey") OVER (
                               PARTITION BY c."ServerId", c."Port"
                               ORDER BY c."DeclaredAt", c."Source", c."Id") AS kept_owner_key
                    FROM "ServerPortReservations" AS c
                    WHERE c."Source" IN (0, 1)
                      AND lower(btrim(c."Protocol")) IS DISTINCT FROM 'udp'
                ) AS ranked
                WHERE loser."Id" = ranked.reservation_id
                  AND ranked.owner_key <> ranked.kept_owner_key;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: the deleted claims are not kept. They were the losing side of a race that the
            // current code can no longer produce, so rolling back leaves the registry with one holder per port.
        }
    }
}
