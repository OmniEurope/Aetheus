// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>R-33: the project's pipelines a newer version of their template exists for, each with the link to the
/// fleet's update preview. Nothing is updated here: the preview shows the change and the user applies it.</summary>
public partial class PipelineSetupTemplateUpdates
{
    /// <summary>The largest page the fleet API serves.</summary>
    private const int PageSize = 200;

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    private IReadOnlyList<PipelineFleetItemDto> _outdated = [];
    private int? _loadedProjectId;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedProjectId == ProjectId) return;
        _loadedProjectId = ProjectId;
        try
        {
            var page = await Api.Pipelines.GetPipelineFleetAsync(
                1, PageSize, projectId: ProjectId, freshness: PipelineFleetFreshness.Outdated);
            _outdated = [.. page.Items.Where(item => item.LatestVersion is not null && item.PinnedVersion is not null)
                .OrderBy(item => item.PipelineName, StringComparer.Ordinal)];
        }
        catch (Exception exception) when (RunParametersReading.IsTransportFailure(exception))
        {
            // Mandatory: an unread fleet claims nothing; the fleet page still lists the updates.
            _outdated = [];
        }
    }

    internal static string UpdateUrl(PipelineFleetItemDto item) =>
        $"/pipelines/{item.PipelineId}/template/update/{item.LatestVersion}";
}
