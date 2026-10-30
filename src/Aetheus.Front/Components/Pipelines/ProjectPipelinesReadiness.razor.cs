// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Pipelines;

/// <summary> The readiness of the pipelines a project already has, read with the same detection as
/// the setup wizard (missing adapter scripts and scanner configuration, no runner, a missing
/// environment, a library or vault a template requires), shown above the project's pipelines.
/// </summary>
public partial class ProjectPipelinesReadiness
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    /// <summary>Recette R3-68: raised once the first verdict is read (or failed to be), so the page can show banners and list together.</summary>
    [Parameter] public EventCallback Loaded { get; set; }

    /// <summary>R-68: the project's overview shows only the values still to fill (empty variables and secrets), not the
    /// other findings, which stay on the pipelines page.</summary>
    [Parameter] public bool EmptyValuesOnly { get; set; }

    private PipelineSetupReadinessDto? _readiness;
    private int? _loadedProjectId;

    private IReadOnlyList<PipelineSetupReadinessCheckDto> Checks => EmptyValuesOnly
        ? [.. (_readiness?.Checks ?? []).Where(check =>
            check.Kind is PipelineSetupReadinessKind.EmptyLibraryValues or PipelineSetupReadinessKind.EmptyVaultSecrets)]
        : _readiness?.Checks ?? [];

    private string TitleKey => EmptyValuesOnly ? "ProjectValuesToFill"
        : _readiness?.HasBlocking == true ? "ProjectPipelinesNotReady" : "ProjectPipelinesToVerify";

    /// <summary>The pages opened to fix a finding come back to this project's pipelines.</summary>
    private string ReturnUrl => EmptyValuesOnly ? $"/projects/{ProjectId}" : $"/projects/{ProjectId}/pipelines";

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedProjectId == ProjectId) return;
        _loadedProjectId = ProjectId;
        await LoadAsync();
        // The banners are drawn first, then the page swaps its loader for the list: the list never moves under them.
        StateHasChanged();
        await Loaded.InvokeAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _readiness = await Api.Pipelines.GetProjectReadinessAsync(ProjectId);
        }
        catch (HttpRequestException)
        {
            // An unread verdict draws nothing rather than a claim about the project; the run itself
            // still asks before launching (PipelineRunGate).
            _readiness = null;
        }
    }
}
