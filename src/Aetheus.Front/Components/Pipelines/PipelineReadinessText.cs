// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Frozen;

namespace Aetheus.Front.Components.Pipelines;

/// <summary> The wording keys and fix pages of each readiness finding, shared by the setup wizard's
/// summary, the project's pipelines page and the confirmation before a run, so the three say the
/// same thing about the same cause. </summary>
internal static class PipelineReadinessText
{
    public static string TitleKey(PipelineSetupReadinessKind kind) => kind switch
    {
        PipelineSetupReadinessKind.NoRepository => "ReadinessNoRepository",
        PipelineSetupReadinessKind.EmptyBranch => "ReadinessEmptyBranch",
        PipelineSetupReadinessKind.MissingAdapterScripts => "ReadinessMissingAdapterScripts",
        PipelineSetupReadinessKind.NoRunnerConfigured => "ReadinessNoRunner",
        PipelineSetupReadinessKind.NoDeployRunnerConfigured => "ReadinessNoDeployRunner",
        // Each missing dependency has its own description and destination so the proposed action
        // matches the cause.
        PipelineSetupReadinessKind.MissingRequiredLibraries => "ReadinessMissingRequiredLibraries",
        PipelineSetupReadinessKind.MissingRequiredVaults => "ReadinessMissingRequiredVaults",
        PipelineSetupReadinessKind.MissingRequiredCapabilities => "ReadinessMissingRequiredCapabilities",
        PipelineSetupReadinessKind.MissingScannerConfiguration => "ReadinessMissingScannerConfiguration",
        PipelineSetupReadinessKind.ExternalRepositoryNotInspected => "ReadinessExternalRepositoryNotInspected",
        _ => "ReadinessMissingEnvironments"
    };

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
        PipelineSetupReadinessKind.MissingRequiredLibraries => "VariableLibraries",
        PipelineSetupReadinessKind.MissingRequiredVaults => "Vaults",
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
        PipelineSetupReadinessKind.MissingRequiredLibraries => $"/projects/{projectId}/libraries",
        PipelineSetupReadinessKind.MissingRequiredVaults => $"/projects/{projectId}/vaults",
        _ => null
    };
}
