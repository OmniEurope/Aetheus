using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class RequireUserEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PLAN-003 P3-4 (owner decision 2026-10-28): people sign in by e-mail only, so every account has one.
            // An address is never invented and an account is never removed here: an account without an e-mail
            // (NULL or blank) or two accounts sharing one (case-insensitive) stop the migration, naming the user
            // ids, before the column changes; the transaction leaves the database as it was. The table is locked
            // against writes first, so no row can slip in between the check and the ALTER.
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: the only schema change is SET NOT NULL plus the not-blank check
            // below, both narrowing what may be written. Reads by the colour still serving are unaffected. Every
            // write path of the release that introduced e-mail sign-in (P3-2: UserService create and update,
            // external-login provisioning, the seed) already refuses a missing or blank address, so it never
            // writes what this refuses. A colour older than P3-2 could still try to create or edit an account
            // without an address during the switch: that write is now refused by the database (an error answer,
            // nothing stored) instead of storing an account nobody can sign in with. Written as raw SQL because
            // the gate refuses EF's AlterColumn, which cannot tell this narrowing apart from a breaking one.
            migrationBuilder.Sql("""
                LOCK TABLE "Users" IN SHARE ROW EXCLUSIVE MODE;

                DO $$
                DECLARE
                    without_email text;
                    sharing_email text;
                BEGIN
                    SELECT string_agg("Id"::text, ', ' ORDER BY "Id") INTO without_email
                    FROM "Users"
                    WHERE "Email" IS NULL OR btrim("Email") = '';

                    SELECT string_agg(accounts, '; ' ORDER BY accounts) INTO sharing_email
                    FROM (
                        SELECT string_agg("Id"::text, ', ' ORDER BY "Id") AS accounts
                        FROM "Users"
                        WHERE "Email" IS NOT NULL AND btrim("Email") <> ''
                        GROUP BY lower(btrim("Email"))
                        HAVING count(*) > 1
                    ) duplicates;

                    IF without_email IS NOT NULL OR sharing_email IS NOT NULL THEN
                        RAISE EXCEPTION 'Required sign-in e-mail migration blocked, nothing was changed. Accounts without an e-mail address (user ids): %. Accounts sharing one e-mail address, case-insensitive (user ids per address): %. Give each account its own address or remove the extra accounts first.',
                            coalesce(without_email, 'none'), coalesce(sharing_email, 'none');
                    END IF;
                END $$;

                ALTER TABLE "Users" ALTER COLUMN "Email" SET NOT NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_Email_NotBlank",
                table: "Users",
                sql: "btrim(\"Email\") <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_Email_NotBlank",
                table: "Users");

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Users",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320);
        }
    }
}
