// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Readiness wording is localized through <see cref="PipelineReadinessChecks"/> while the
/// API supplies structured causes. Required libraries and vaults can be created with empty values
/// and remain marked for user review.</summary>
public partial class PipelineSetupReadinessPanel
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private PipelineSetupProvisionState ProvisionState { get; set; } = default!;

    [Parameter] public PipelineSetupReadinessDto? Readiness { get; set; }
    [Parameter] public bool Loading { get; set; }

    /// <summary>Project the remediation links point at; without it a link would target no project.</summary>
    [Parameter, EditorRequired] public int ProjectId { get; set; }

    /// <summary>Templates the wizard is about to create, the same list the readiness check read.</summary>
    [Parameter] public IReadOnlyList<string> TemplateNames { get; set; } = [];

    /// <summary>The wizard's summary with its choices, where the pages opened from here come back to.</summary>
    [Parameter] public string? ReturnUrl { get; set; }

    /// <summary>Raised after the project changed from here (requirements provisioned, a repository
    /// created), so the host re-runs the readiness check.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    private bool _provisioning;
    private string? _provisionError;

    private IReadOnlyList<ProvisionedElement> Provisioned => ProvisionState.For(ProjectId);
    private bool AllChecked => ProvisionState.AllChecked(ProjectId);

    private bool CanProvision => Readiness is not null && TemplateNames.Count > 0
        && Readiness.Checks.Any(check => check.Kind is PipelineSetupReadinessKind.MissingRequiredLibraries
            or PipelineSetupReadinessKind.MissingRequiredVaults);

    private string CheckHref(ProvisionedElement element) => LocalReturnUrl.Append(element.Path, ReturnUrl);

    /// <summary>The click opens the element (the link's own navigation); recording it is what marks
    /// the element checked when the user comes back to the summary.</summary>
    private void MarkChecked(ProvisionedElement element) =>
        ProvisionState.MarkChecked(ProjectId, element.Kind, element.Id);

    private async Task ProvisionAsync()
    {
        _provisioning = true;
        _provisionError = null;
        try
        {
            var provisioned = await Api.Pipelines.ProvisionSetupRequirementsAsync(ProjectId, TemplateNames);
            if (provisioned is null)
            {
                _provisionError = L["PipelineSetupProvisionFailed"].Value;
                return;
            }

            ProvisionState.Record(ProjectId, provisioned);
            await OnChanged.InvokeAsync();
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
