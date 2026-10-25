// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Frozen;

namespace Aetheus.Front.Components.Pipelines;

/// <summary> The wording keys and fix pages of each readiness finding, shared by the setup wizard's
/// summary, the project's pipelines page and the confirmation before a run, so the three say the
/// same thing about the same cause. </summary>
internal static class PipelineReadinessText
{
    /// <summary>The title key of each finding; a lookup instead of a switch, which the new repository
    /// checks (R-10 to R-15) had pushed past the grade A complexity bound.</summary>
    private static readonly FrozenDictionary<PipelineSetupReadinessKind, string> TitleKeys =
        new Dictionary<PipelineSetupReadinessKind, string>
        {
            [PipelineSetupReadinessKind.NoRepository] = "ReadinessNoRepository",
            [PipelineSetupReadinessKind.EmptyBranch] = "ReadinessEmptyBranch",
            [PipelineSetupReadinessKind.MissingAdapterScripts] = "ReadinessMissingAdapterScripts",
            [PipelineSetupReadinessKind.NoRunnerConfigured] = "ReadinessNoRunner",
            [PipelineSetupReadinessKind.NoDeployRunnerConfigured] = "ReadinessNoDeployRunner",
            // Each missing dependency has its own description and destination so the proposed action
            // matches the cause.
            [PipelineSetupReadinessKind.MissingRequiredLibraries] = "ReadinessMissingRequiredLibraries",
            [PipelineSetupReadinessKind.MissingRequiredVaults] = "ReadinessMissingRequiredVaults",
            [PipelineSetupReadinessKind.MissingRequiredCapabilities] = "ReadinessMissingRequiredCapabilities",
            [PipelineSetupReadinessKind.MissingScannerConfiguration] = "ReadinessMissingScannerConfiguration",
            [PipelineSetupReadinessKind.ExternalRepositoryNotInspected] = "ReadinessExternalRepositoryNotInspected",
            [PipelineSetupReadinessKind.EmptyLibraryValues] = "ReadinessEmptyLibraryValues",
            [PipelineSetupReadinessKind.EmptyVaultSecrets] = "ReadinessEmptyVaultSecrets",
            [PipelineSetupReadinessKind.NoDotNetSolution] = "ReadinessNoDotNetSolution",
            [PipelineSetupReadinessKind.NoDotNetTestProject] = "ReadinessNoDotNetTestProject",
            [PipelineSetupReadinessKind.NoComposeFile] = "ReadinessNoComposeFile",
            [PipelineSetupReadinessKind.NoDockerRunner] = "ReadinessNoDockerRunner"
        }.ToFrozenDictionary();

    public static string TitleKey(PipelineSetupReadinessKind kind) =>
        TitleKeys.TryGetValue(kind, out var key) ? key : "ReadinessMissingEnvironments";

    public static string HintKey(PipelineSetupReadinessKind kind) => $"{TitleKey(kind)}Hint";

    /// <summary> What a missing file has to do, so the author of a new project knows what to write
    /// rather than only its name. Null for a file the templates do not document. </summary>
    public static string? ItemRoleKey(PipelineSetupReadinessKind kind, string item)
    {
        var fileName = item[(item.LastIndexOf('/') + 1)..];
        return kind switch
        {
            PipelineSetupReadinessKind.MissingScannerConfiguration
                or PipelineSetupReadinessKind.ExternalRepositoryNotInspected when fileName == ".gitleaks.toml"
                => "ReadinessScannerFileGitleaks",
            PipelineSetupReadinessKind.MissingAdapterScripts
                or PipelineSetupReadinessKind.ExternalRepositoryNotInspected => AdapterRoleKey(fileName),
            _ => null
        };
    }

    /// <summary>What a required library or vault is for (R2-13), by the environment its name ends with
    /// (<c>.share</c>, <c>.qa</c>, <c>.prod</c>, the names the setup derives from the project).</summary>
    public static string PurposeKey(bool isVault, string name)
    {
        var suffix = name[(name.LastIndexOf('.') + 1)..].ToLowerInvariant();
        return (isVault, suffix) switch
        {
            (false, "share") => "PipelineSetupPurposeLibraryShare",
            (false, "qa") => "PipelineSetupPurposeLibraryQa",
            (false, "prod") => "PipelineSetupPurposeLibraryProd",
            (false, _) => "PipelineSetupPurposeLibraryOther",
            (true, "qa") => "PipelineSetupPurposeVaultQa",
            (true, "prod") => "PipelineSetupPurposeVaultProd",
            (true, _) => "PipelineSetupPurposeVaultOther"
        };
    }

    /// <summary>The tooltip of a required library or vault: what it is for and the keys the setup creates in
    /// it (R-14). A resource the setup derives no keys for says the generic templates require none.</summary>
    public static string Purpose(IStringLocalizer<AppStrings> localizer, bool isVault, string name)
    {
        var key = PurposeKey(isVault, name);
        var purpose = localizer[key].Value;
        return key.EndsWith("Other", StringComparison.Ordinal)
            ? $"{purpose} {localizer["PipelineSetupPurposeNoKeys"].Value}"
            : purpose;
    }

    /// <summary>The creation page of one missing library or vault, its name already filled in, so the
    /// link leads to the element concerned rather than to the list of all of them (R2-22). Null for
    /// any other kind of finding.</summary>
    public static string? ItemCreationHref(PipelineSetupReadinessKind kind, string item, int projectId, string? returnUrl)
    {
        var page = kind switch
        {
            PipelineSetupReadinessKind.MissingRequiredLibraries => "/variable-libraries/new",
            PipelineSetupReadinessKind.MissingRequiredVaults => "/vaults/new",
            _ => null
        };
        return page is null
            ? null
            : LocalReturnUrl.Append($"{page}?projectId={projectId}&name={Uri.EscapeDataString(item)}", returnUrl);
    }

    /// <summary>The page of the library or vault holding one empty variable or secret (R2-27), so the item is a
    /// link to the very element to fill. Null for any other finding, or when the holder is not known.</summary>
    public static string? ItemOwnerHref(PipelineSetupReadinessCheckDto check, string item, string? returnUrl)
    {
        ArgumentNullException.ThrowIfNull(check);
        var page = check.Kind switch
        {
            PipelineSetupReadinessKind.EmptyLibraryValues => "/variable-libraries",
            PipelineSetupReadinessKind.EmptyVaultSecrets => "/vaults",
            _ => null
        };
        return page is not null && check.ItemIds.TryGetValue(item, out var id)
            ? LocalReturnUrl.Append($"{page}/{id}", returnUrl)
            : null;
    }

    /// <summary>R-10: the page of the library or vault holding an empty value, or null when it could not be told.</summary>
    public static string? EmptyValueOwnerHref(bool isVault, PipelineSetupReadinessEmptyValueDto row, string? returnUrl)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.OwnerId is { } id ? LocalReturnUrl.Append($"{OwnerPage(isVault)}/{id}", returnUrl) : null;
    }

    /// <summary>R-10: the same page opened on the entry itself, its Key column filtered on the key.</summary>
    public static string? EmptyValueKeyHref(bool isVault, PipelineSetupReadinessEmptyValueDto row, string? returnUrl)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.OwnerId is { } id
            ? LocalReturnUrl.Append($"{OwnerPage(isVault)}/{id}?key={Uri.EscapeDataString(row.Key)}", returnUrl)
            : null;
    }

    /// <summary>R-10: the pipeline that reads an empty value, or null for a template not created yet.</summary>
    public static string? EmptyValuePipelineHref(PipelineSetupReadinessEmptyValueDto row, string? returnUrl)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.PipelineId is { } id ? LocalReturnUrl.Append($"/pipelines/{id}", returnUrl) : null;
    }

    private static string OwnerPage(bool isVault) => isVault ? "/vaults" : "/variable-libraries";

    /// <summary>The text key of each documented adapter script; a lookup instead of a string switch.</summary>
    private static readonly FrozenDictionary<string, string> AdapterRoleKeys =
        new Dictionary<string, string>
        {
            ["ci.sh"] = "ReadinessAdapterCi",
            ["quality.sh"] = "ReadinessAdapterQuality",
            ["qa.sh"] = "ReadinessAdapterQa",
            ["qa-extended.sh"] = "ReadinessAdapterQaExtended",
            ["deploy-prod.sh"] = "ReadinessAdapterDeployProd",
            ["release-fast.sh"] = "ReadinessAdapterReleaseFast",
            ["publish-packages.sh"] = "ReadinessAdapterPublishPackages"
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static string? AdapterRoleKey(string fileName) =>
        AdapterRoleKeys.TryGetValue(fileName, out var key) ? key
        : fileName.StartsWith("package-", StringComparison.Ordinal) ? "ReadinessAdapterPackage"
        : null;

    /// <summary>The label of the fix of a finding; null when the finding has none on this page.</summary>
    public static string? RemediationKey(PipelineSetupReadinessKind kind) => kind switch
    {
        PipelineSetupReadinessKind.NoRepository => "CreateGitRepository",
        PipelineSetupReadinessKind.EmptyBranch or PipelineSetupReadinessKind.MissingAdapterScripts
            or PipelineSetupReadinessKind.MissingScannerConfiguration => "GitRepositories",
        PipelineSetupReadinessKind.ExternalRepositoryNotInspected => "ExternalRepository",
        PipelineSetupReadinessKind.MissingEnvironments => "CreateEnvironment",
        PipelineSetupReadinessKind.NoRunnerConfigured or PipelineSetupReadinessKind.NoDeployRunnerConfigured => "AddServer",
        // Empty values are filled on the list of the existing libraries and vaults.
        PipelineSetupReadinessKind.EmptyLibraryValues => "VariableLibraries",
        PipelineSetupReadinessKind.EmptyVaultSecrets => "Vaults",
        // A missing library or vault is fixed from its own creation page, one link per name
        // (ItemCreationKey), never from the list of all of them.
        _ => null
    };

    /// <summary>The label of the link that creates one missing library or vault; null for other findings.</summary>
    public static string? ItemCreationKey(PipelineSetupReadinessKind kind) => kind switch
    {
        PipelineSetupReadinessKind.MissingRequiredLibraries => "CreateThisLibrary",
        PipelineSetupReadinessKind.MissingRequiredVaults => "CreateThisVault",
        _ => null
    };

    /// <summary>Finding links lead to the relevant configuration page or repository browser.
    /// Creation pages preserve <paramref name="returnUrl"/>; a missing repository is created in a
    /// dialog.</summary>
    public static string? RemediationHref(PipelineSetupReadinessKind kind, int projectId, string? returnUrl) => kind switch
    {
        PipelineSetupReadinessKind.EmptyBranch or PipelineSetupReadinessKind.MissingAdapterScripts
            or PipelineSetupReadinessKind.MissingScannerConfiguration => $"/git-repositories?projectId={projectId}",
        PipelineSetupReadinessKind.ExternalRepositoryNotInspected => $"/projects/{projectId}/external-repo",
        PipelineSetupReadinessKind.MissingEnvironments
            => LocalReturnUrl.Append($"/environments/new?projectId={projectId}", returnUrl),
        PipelineSetupReadinessKind.NoRunnerConfigured or PipelineSetupReadinessKind.NoDeployRunnerConfigured
            => LocalReturnUrl.Append("/servers/add-agent", returnUrl),
        PipelineSetupReadinessKind.EmptyLibraryValues => $"/projects/{projectId}/libraries",
        PipelineSetupReadinessKind.EmptyVaultSecrets => $"/projects/{projectId}/vaults",
        _ => null
    };
}
