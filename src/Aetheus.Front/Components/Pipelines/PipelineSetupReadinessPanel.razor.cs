// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Renders the pre-creation readiness verdict. The backend sends a cause and its raw items, the
/// wording and the page that fixes each cause live here so the panel stays localizable and the API
/// stays free of user-facing prose.
///
/// PLAN-003 lot 30 / D22: for the two causes a click can fix (a required library or vault that does
/// not exist), the panel offers to create them, keys copied, values empty, instead of sending the
/// operator to recreate each one by hand.
/// </summary>
public partial class PipelineSetupReadinessPanel
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;

    [Parameter] public PipelineSetupReadinessDto? Readiness { get; set; }
    [Parameter] public bool Loading { get; set; }

    /// <summary>Project the remediation links point at; without it a link would target no project.</summary>
    [Parameter, EditorRequired] public int ProjectId { get; set; }

    /// <summary>Templates the wizard is about to create, the same list the readiness check read.</summary>
    [Parameter] public IReadOnlyList<string> TemplateNames { get; set; } = [];

    /// <summary>Raised after a successful provisioning, so the host re-runs the readiness check.</summary>
    [Parameter] public EventCallback OnProvisioned { get; set; }

    private bool _provisioning;
    private string? _provisionError;
    private PipelineRequirementsProvisionResultDto? _provisioned;

    private bool CanProvision => Readiness is not null && TemplateNames.Count > 0
        && Readiness.Checks.Any(check => check.Kind is PipelineSetupReadinessKind.MissingRequiredLibraries
            or PipelineSetupReadinessKind.MissingRequiredVaults);

    private static string TitleKey(PipelineSetupReadinessKind kind) => kind switch
    {
        PipelineSetupReadinessKind.NoRepository => "ReadinessNoRepository",
        PipelineSetupReadinessKind.EmptyBranch => "ReadinessEmptyBranch",
        PipelineSetupReadinessKind.MissingAdapterScripts => "ReadinessMissingAdapterScripts",
        PipelineSetupReadinessKind.NoRunnerConfigured => "ReadinessNoRunner",
        PipelineSetupReadinessKind.NoDeployRunnerConfigured => "ReadinessNoDeployRunner",
        // These three used to fall through to "missing environments", with an "add a server" link:
        // a missing library was announced as a missing environment and sent the reader to the wrong
        // page. Each now says what it is.
        PipelineSetupReadinessKind.MissingRequiredLibraries => "ReadinessMissingRequiredLibraries",
        PipelineSetupReadinessKind.MissingRequiredVaults => "ReadinessMissingRequiredVaults",
        PipelineSetupReadinessKind.MissingRequiredCapabilities => "ReadinessMissingRequiredCapabilities",
        PipelineSetupReadinessKind.MissingScannerConfiguration => "ReadinessMissingScannerConfiguration",
        _ => "ReadinessMissingEnvironments"
    };

    private static string HintKey(PipelineSetupReadinessKind kind) => $"{TitleKey(kind)}Hint";

    /// <summary>
    /// Recette R2-121: what a missing file has to do, so the author of a new project knows what to
    /// write rather than only its name. Null for a file the templates do not document.
    /// </summary>
    private string? ItemRole(PipelineSetupReadinessKind kind, string item)
    {
        var fileName = item[(item.LastIndexOf('/') + 1)..];
        var key = kind switch
        {
            PipelineSetupReadinessKind.MissingScannerConfiguration when fileName == ".gitleaks.toml"
                => "ReadinessScannerFileGitleaks",
            PipelineSetupReadinessKind.MissingAdapterScripts => AdapterRoleKey(fileName),
            _ => null
        };
        return key is null ? null : L[key].Value;
    }

    /// <summary>The text key of each documented adapter script; a lookup instead of a string switch.</summary>
    private static readonly System.Collections.Frozen.FrozenDictionary<string, string> AdapterRoleKeys =
        System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new Dictionary<string, string>
        {
            ["ci.sh"] = "ReadinessAdapterCi",
            ["quality.sh"] = "ReadinessAdapterQuality",
            ["qa.sh"] = "ReadinessAdapterQa",
            ["qa-extended.sh"] = "ReadinessAdapterQaExtended",
            ["deploy-prod.sh"] = "ReadinessAdapterDeployProd",
            ["release-fast.sh"] = "ReadinessAdapterReleaseFast",
            ["publish-packages.sh"] = "ReadinessAdapterPublishPackages"
        }, StringComparer.Ordinal);

    private static string? AdapterRoleKey(string fileName) =>
        AdapterRoleKeys.TryGetValue(fileName, out var key) ? key
        : fileName.StartsWith("package-", StringComparison.Ordinal) ? "ReadinessAdapterPackage"
        : null;

    private static string RemediationKey(PipelineSetupReadinessKind kind) => kind switch
    {
        PipelineSetupReadinessKind.NoRepository => "CreateGitRepository",
        PipelineSetupReadinessKind.EmptyBranch or PipelineSetupReadinessKind.MissingAdapterScripts
            or PipelineSetupReadinessKind.MissingScannerConfiguration
            => "GitRepositories",
        PipelineSetupReadinessKind.MissingEnvironments => "CreateEnvironment",
        _ => "AddServer"
    };

    /// <summary>
    /// A missing adapter script has no page that writes it - the fix is a commit - so that row links
    /// to the repository browser where the author can see what is actually there. A missing library
    /// or vault has no link: the create button below is its remedy, and a capability has no page.
    /// </summary>
    private string? RemediationHref(PipelineSetupReadinessKind kind) => kind switch
    {
        PipelineSetupReadinessKind.NoRepository => $"/git-repositories?projectId={ProjectId}&create=true",
        PipelineSetupReadinessKind.EmptyBranch or PipelineSetupReadinessKind.MissingAdapterScripts
            or PipelineSetupReadinessKind.MissingScannerConfiguration
            => $"/git-repositories?projectId={ProjectId}",
        PipelineSetupReadinessKind.MissingEnvironments => $"/environments/new?projectId={ProjectId}",
        PipelineSetupReadinessKind.NoRunnerConfigured
            or PipelineSetupReadinessKind.NoDeployRunnerConfigured => "/servers/add-agent",
        _ => null
    };

    private async Task ProvisionAsync()
    {
        _provisioning = true;
        _provisionError = null;
        try
        {
            _provisioned = await Api.Pipelines.ProvisionSetupRequirementsAsync(ProjectId, TemplateNames);
            if (_provisioned is null)
            {
                _provisionError = L["PipelineSetupProvisionFailed"].Value;
                return;
            }

            await OnProvisioned.InvokeAsync();
        }
        catch (HttpRequestException)
        {
            _provisionError = L["PipelineSetupProvisionFailed"].Value;
        }
        finally
        {
            _provisioning = false;
        }
    }
}
