// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// One step of a two-release contraction that is still due. <paramref name="RequiredAfterMigration"/> is the
/// migration whose release must be live before the step can ship (the previous colour, V-1, then already
/// carries it); null when the step waits for the earlier step <paramref name="DependsOnId"/> instead.
/// </summary>
internal sealed record PendingContractionStep(
    string Id,
    string Table,
    IReadOnlyList<string> Columns,
    SchemaContractionStep Step,
    string ExpandMigration,
    string? RequiredAfterMigration,
    string? DependsOnId = null);

/// <summary>
/// The single source of the expand/contract plan: which contractions are still due and which migrations are
/// reviewed contract drops. The run page, the environment page and the architecture guards all read it, so the
/// plan lives here and nowhere else (docs/contracts/deployment.md describes the rule and points here).
///
/// Retiring a column the previous colour still maps takes two releases. Release N unmaps the property and, when
/// the column is NOT NULL, gives it a database default in a reviewed migration, so the previous colour (which
/// still writes it) and N (which omits it) both work against the shared schema. Release N+1, whose V-1 is N,
/// drops the column through reviewed raw SQL named in <c>ExpandContractMigrationAuditTests</c>.
///
/// Maintenance: when a step ships, replace its entry by the next step of the same contraction (the unmap becomes
/// the drop, which then has <c>RequiredAfterMigration</c> set to the unmap migration) or, when the drop ships,
/// remove the entry and add the drop migration to <see cref="ContractMigrations"/>. The guards fail when this
/// list and the entity model or the migration tree disagree.
/// </summary>
internal static class SchemaContractionRegistry
{
    /// <summary>
    /// Migrations that drop what the previous colour had stopped mapping: reviewed contract drops, shipped.
    /// <c>ExpandContractMigrationAuditTests</c> requires every one of them to be named here.
    /// </summary>
    public static IReadOnlyList<string> ContractMigrations { get; } =
    [
        // The environment branch-advance columns were absent from the preceding release's mapping.
        "20261010093813_DropRetiredEnvironmentBranchAdvanceColumns"
    ];

    public static IReadOnlyList<PendingContractionStep> Pending { get; } =
    [
        // Recette R-287: the secret history keeps no value. Step 1 (unmap, column default '') shipped as
        // 20261010094003_UnmapVaultSecretVersionEncryptedValue; the column stays NOT NULL until the drop.
        new PendingContractionStep(
            "vault-secret-version-encrypted-value-drop",
            "VaultSecretVersions",
            ["EncryptedValue"],
            SchemaContractionStep.Drop,
            ExpandMigration: "20260924183556_VaultSecretHistoryWithoutValues",
            RequiredAfterMigration: "20261010094003_UnmapVaultSecretVersionEncryptedValue"),

        // Decision D13, BACK-013: the three-level ProtectionLevel column replaces RequirePullRequest. The entity
        // still maps it and keeps it equal to ProtectionLevel != NotProtected because the previous colour reads it.
        new PendingContractionStep(
            "branch-protection-require-pull-request-unmap",
            "BranchProtectionRules",
            ["RequirePullRequest"],
            SchemaContractionStep.Unmap,
            ExpandMigration: "20261008102123_BranchProtectionLevels",
            RequiredAfterMigration: "20261008102123_BranchProtectionLevels"),
        new PendingContractionStep(
            "branch-protection-require-pull-request-drop",
            "BranchProtectionRules",
            ["RequirePullRequest"],
            SchemaContractionStep.Drop,
            ExpandMigration: "20261008102123_BranchProtectionLevels",
            RequiredAfterMigration: null,
            DependsOnId: "branch-protection-require-pull-request-unmap"),

        // ADR-023 amendment (b): the API columns replace these. The entity still maps them and writes '', [] and
        // an encrypted {} because the previous colour maps them NOT NULL (and decrypts the third).
        new PendingContractionStep(
            "ai-runner-profile-legacy-columns-unmap",
            "AiRunnerProfiles",
            ["Binary", "ArgsTemplateJson", "EnvironmentJsonEncrypted"],
            SchemaContractionStep.Unmap,
            ExpandMigration: "20261011110402_AddAiRunnerProfileApiSettings",
            RequiredAfterMigration: "20261011110402_AddAiRunnerProfileApiSettings"),
        new PendingContractionStep(
            "ai-runner-profile-legacy-columns-drop",
            "AiRunnerProfiles",
            ["Binary", "ArgsTemplateJson", "EnvironmentJsonEncrypted"],
            SchemaContractionStep.Drop,
            ExpandMigration: "20261011110402_AddAiRunnerProfileApiSettings",
            RequiredAfterMigration: null,
            DependsOnId: "ai-runner-profile-legacy-columns-unmap")
    ];

    /// <summary>True when the migration is a reviewed contract drop already shipped.</summary>
    public static bool IsContractMigration(string migrationId) =>
        ContractMigrations.Contains(migrationId, StringComparer.Ordinal);
}
