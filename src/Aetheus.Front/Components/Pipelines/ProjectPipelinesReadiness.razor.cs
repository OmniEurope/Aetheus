// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R1-28: the readiness of the pipelines a project already has, read with the same detection
/// as the setup wizard (missing adapter scripts and scanner configuration, no runner, a missing
/// environment, a library or vault a template requires), shown above the project's pipelines.
/// </summary>
public partial class ProjectPipelinesReadiness
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    private PipelineSetupReadinessDto? _readiness;
    private int? _loadedProjectId;

    /// <summary>The pages opened to fix a finding come back to this project's pipelines.</summary>
    private string ReturnUrl => $"/projects/{ProjectId}/pipelines";

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedProjectId == ProjectId) return;
        _loadedProjectId = ProjectId;
        await LoadAsync();
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
