// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R1-34: says where a pipeline's definition is stored, read from the same source the run
/// uses (<c>api/pipelines/{id}/source</c>): none means the definition is authoritative in Aetheus'
/// database (a project without an internal repository, or a pipeline owned by an environment or a
/// server); a source names the Git file the definition is written to.
/// </summary>
public partial class PipelineDefinitionStorage
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int PipelineId { get; set; }

    private bool _loaded;
    private PipelineSourceDto? _source;
    private int? _loadedFor;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedFor == PipelineId) return;
        _loadedFor = PipelineId;
        try
        {
            (_loaded, _source) = await Api.Pipelines.LookUpPipelineSourceAsync(PipelineId);
        }
        catch (HttpRequestException)
        {
            // Unread: say nothing rather than a storage place the page does not know.
            (_loaded, _source) = (false, null);
        }
    }
}
