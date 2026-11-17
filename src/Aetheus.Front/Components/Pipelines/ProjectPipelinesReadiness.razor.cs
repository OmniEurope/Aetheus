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

    /// <summary>R-9: on the page of one pipeline, the verdict of that pipeline only, and only what it reads that nothing
    /// defines; the project's other findings stay on its pipelines page.</summary>
    [Parameter] public int? PipelineId { get; set; }

    private PipelineSetupReadinessDto? _readiness;
    private int? _loadedProjectId;
    private int? _loadedPipelineId;

    // R-36: the page of a pipeline that starts another on success is where that option is set, so it also says when the
    // follow-up will be refused for want of a deployment server.
    private IReadOnlyList<PipelineSetupReadinessCheckDto> Checks => PipelineId is not null
        ? [.. (_readiness?.Checks ?? []).Where(check =>
            check.Kind is PipelineSetupReadinessKind.UndefinedVariables || IsChainedFollowUp(check))]
        : EmptyValuesOnly
            ? [.. (_readiness?.Checks ?? []).Where(check =>
                check.Kind is PipelineSetupReadinessKind.EmptyLibraryValues or PipelineSetupReadinessKind.EmptyVaultSecrets
                    or PipelineSetupReadinessKind.UndefinedVariables)]
            : _readiness?.Checks ?? [];

    private string TitleKey => (EmptyValuesOnly || PipelineId is not null)
        && !Checks.Any(IsChainedFollowUp) ? "ProjectValuesToFill"
        : _readiness?.HasBlocking == true ? "ProjectPipelinesNotReady" : "ProjectPipelinesToVerify";

    /// <summary>R-36: the follow-up an <c>on_success:</c> starts will be refused (no deployment server, or another required
    /// parameter nothing supplies).</summary>
    private static bool IsChainedFollowUp(PipelineSetupReadinessCheckDto check) =>
        check.Kind is PipelineSetupReadinessKind.ChainedDeployServerMissing
            or PipelineSetupReadinessKind.ChainedRequiredParameterMissing;

    /// <summary>The pages opened to fix a finding come back to this project's pipelines.</summary>
    private string ReturnUrl => PipelineId is { } pipelineId ? $"/pipelines/{pipelineId}"
        : EmptyValuesOnly ? $"/projects/{ProjectId}" : $"/projects/{ProjectId}/pipelines";

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedProjectId == ProjectId && _loadedPipelineId == PipelineId) return;
        _loadedProjectId = ProjectId;
        _loadedPipelineId = PipelineId;
        await LoadAsync();
        // The banners are drawn first, then the page swaps its loader for the list: the list never moves under them.
        StateHasChanged();
        await Loaded.InvokeAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _readiness = PipelineId is { } pipelineId
                ? await Api.Pipelines.GetPipelineReadinessAsync(pipelineId)
                : await Api.Pipelines.GetProjectReadinessAsync(ProjectId);
        }
        catch (HttpRequestException)
        {
            // An unread verdict draws nothing rather than a claim about the project; the run itself
            // still asks before launching (PipelineRunGate).
            _readiness = null;
        }
    }
}
