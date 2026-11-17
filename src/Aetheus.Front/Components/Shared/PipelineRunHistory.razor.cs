// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
namespace Aetheus.Front.Components.Shared;

public partial class PipelineRunHistory
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<PipelineRunSummaryDto> Runs { get; set; } = [];
    [Parameter] public int? ProjectId { get; set; }
    [Parameter] public int? ServerId { get; set; }

    private IReadOnlyList<PipelineRunSummaryDto> DisplayRuns => Runs.Take(5).Reverse().ToList();

    private string RunHref(int runId) => PipelineRunPresentation.Href(runId, ProjectId, ServerId);

    private string RunTitle(PipelineRunSummaryDto run) =>
        $"#{run.BuildNumber} - {L.Localize(run.Status)} - {run.StartedAt:g}";
}
