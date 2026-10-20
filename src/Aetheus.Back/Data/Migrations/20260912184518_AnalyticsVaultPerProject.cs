using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnalyticsVaultPerProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: idempotently copy application pseudonymization
            // keys into project analytics vaults without changing schema, source vaults or
            // encrypted values. SQL naming mirrors VaultNameFor so existing pseudonyms and
            // application key identities remain stable.
            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE analytics_vault_move ON COMMIT DROP AS
                SELECT a."Id" AS app_id,
                       a."ProjectId" AS project_id,
                       a."AnalyticsVaultName" AS legacy_name,
                       COALESCE(
                           NULLIF(btrim(left(btrim(regexp_replace(lower(p."Name"), '[^a-z0-9]+', '-', 'g'), '-'), 90), '-'), ''),
                           'project-' || p."Id") || '.analytics' AS vault_name
                FROM "MonitoredApps" AS a
                JOIN "Projects" AS p ON p."Id" = a."ProjectId"
                WHERE a."AnalyticsVaultName" IS NOT NULL
                  AND a."AnalyticsVaultName" NOT LIKE '%.analytics';

                INSERT INTO "Vaults" ("Name", "Description", "ProjectId", "CreatedAt", "UpdatedAt", "RowVersion")
                SELECT m.vault_name,
                       'Aetheus-managed pseudonymization keys for no-banner web analytics, one per app.',
                       m.project_id, now(), now(), gen_random_uuid()
                FROM (SELECT DISTINCT vault_name, project_id FROM analytics_vault_move) AS m
                WHERE NOT EXISTS (
                    SELECT 1 FROM "Vaults" AS v
                    WHERE v."ProjectId" = m.project_id AND v."Name" = m.vault_name);

                INSERT INTO "VaultSecrets" ("VaultId", "Key", "EncryptedValue", "CreatedAt", "UpdatedAt", "ExpiresAt")
                SELECT target."Id",
                       'AETHEUS_WEB_ANALYTICS_PSEUDONYMIZATION_KEY_' || m.app_id,
                       s."EncryptedValue", now(), now(), s."ExpiresAt"
                FROM analytics_vault_move AS m
                JOIN "Vaults" AS legacy ON legacy."ProjectId" = m.project_id AND legacy."Name" = m.legacy_name
                JOIN "VaultSecrets" AS s ON s."VaultId" = legacy."Id"
                    AND s."Key" = 'AETHEUS_WEB_ANALYTICS_PSEUDONYMIZATION_KEY'
                JOIN "Vaults" AS target ON target."ProjectId" = m.project_id AND target."Name" = m.vault_name
                WHERE NOT EXISTS (
                    SELECT 1 FROM "VaultSecrets" AS existing
                    WHERE existing."VaultId" = target."Id"
                      AND existing."Key" = 'AETHEUS_WEB_ANALYTICS_PSEUDONYMIZATION_KEY_' || m.app_id);

                -- Expand/contract: the colour still serving during the cutover, and the previous release
                -- after a return to it, read the unsuffixed key from whatever vault AnalyticsVaultName
                -- names. A project with ONE analytics app therefore also gets the unsuffixed key, same
                -- value, so that release keeps working; with several apps the name would be ambiguous and
                -- the previous release loses public ingest until the next deployment. The contract
                -- release removes this copy.
                INSERT INTO "VaultSecrets" ("VaultId", "Key", "EncryptedValue", "CreatedAt", "UpdatedAt", "ExpiresAt")
                SELECT target."Id", 'AETHEUS_WEB_ANALYTICS_PSEUDONYMIZATION_KEY', own."EncryptedValue", now(), now(), own."ExpiresAt"
                FROM (SELECT project_id, vault_name, min(app_id) AS app_id
                      FROM analytics_vault_move GROUP BY project_id, vault_name HAVING count(*) = 1) AS single
                JOIN "Vaults" AS target ON target."ProjectId" = single.project_id AND target."Name" = single.vault_name
                JOIN "VaultSecrets" AS own ON own."VaultId" = target."Id"
                    AND own."Key" = 'AETHEUS_WEB_ANALYTICS_PSEUDONYMIZATION_KEY_' || single.app_id
                WHERE NOT EXISTS (
                    SELECT 1 FROM "VaultSecrets" AS existing
                    WHERE existing."VaultId" = target."Id" AND existing."Key" = 'AETHEUS_WEB_ANALYTICS_PSEUDONYMIZATION_KEY');

                INSERT INTO "VaultSecretVersions" ("VaultSecretId", "Key", "EncryptedValue", "Version", "ChangedAt", "ChangeType")
                SELECT s."Id", s."Key", s."EncryptedValue", 1, now(), 0
                FROM "VaultSecrets" AS s
                JOIN "Vaults" AS target ON target."Id" = s."VaultId"
                JOIN (SELECT DISTINCT project_id, vault_name FROM analytics_vault_move) AS m
                    ON target."ProjectId" = m.project_id AND target."Name" = m.vault_name
                WHERE NOT EXISTS (SELECT 1 FROM "VaultSecretVersions" AS v WHERE v."VaultSecretId" = s."Id");

                -- Repointed only once its key is really in the project vault: an app whose legacy vault
                -- lost its key keeps its old name and stays exactly as broken as it was, visibly.
                UPDATE "MonitoredApps" AS a
                SET "AnalyticsVaultName" = m.vault_name
                FROM analytics_vault_move AS m
                WHERE a."Id" = m.app_id
                  AND EXISTS (
                      SELECT 1 FROM "VaultSecrets" AS s
                      JOIN "Vaults" AS target ON target."Id" = s."VaultId"
                      WHERE target."ProjectId" = m.project_id AND target."Name" = m.vault_name
                        AND s."Key" = 'AETHEUS_WEB_ANALYTICS_PSEUDONYMIZATION_KEY_' || m.app_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Points each app back at its legacy vault when that vault still exists; the project vaults
            // and their copied keys are left for a person to remove, since other apps may use them.
            migrationBuilder.Sql(
                """
                UPDATE "MonitoredApps" AS a
                SET "AnalyticsVaultName" = 'aetheus-web-analytics-' || a."Id"
                WHERE a."AnalyticsVaultName" LIKE '%.analytics'
                  AND EXISTS (
                      SELECT 1 FROM "Vaults" AS v
                      WHERE v."ProjectId" = a."ProjectId" AND v."Name" = 'aetheus-web-analytics-' || a."Id");
                """);
        }
    }
}
