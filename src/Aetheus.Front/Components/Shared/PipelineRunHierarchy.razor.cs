// SPDX-License-Identifier: EUPL-1.2

using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

public partial class PipelineRunHierarchy
{
    private static OmniTone StatusVariant(PipelineStatus status) => status switch
    {
        PipelineStatus.Success => OmniTone.Success,
        PipelineStatus.Failed => OmniTone.Danger,
        PipelineStatus.Running => OmniTone.Accent,
        PipelineStatus.Partial or PipelineStatus.RolledBack or PipelineStatus.Rejected => OmniTone.Warning,
        _ => OmniTone.Neutral
    };

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<PipelineRunTableItem> Items { get; set; } = [];
    [Parameter] public bool Compact { get; set; }
    [Parameter] public bool ShowDurationInCompact { get; set; }
    [Parameter] public bool GroupByProject { get; set; }
    [Parameter] public int? ServerId { get; set; }
    [Parameter] public int Depth { get; set; }

    private readonly HashSet<int> _expandedRunIds = [];

    private string TreeCssClass =>
        $"pipeline-run-tree{(Compact ? " pipeline-run-tree-compact" : string.Empty)}{(ShowDurationInCompact ? " pipeline-run-tree-with-duration" : string.Empty)}{(GroupByProject ? " pipeline-run-tree-with-project" : string.Empty)}{(Depth > 0 ? " pipeline-run-tree-nested" : string.Empty)}";

    private bool IsExpanded(int runId) => _expandedRunIds.Contains(runId);

    private void Toggle(int runId)
    {
        if (!_expandedRunIds.Add(runId))
            _expandedRunIds.Remove(runId);
    }

    private string PipelineHref(PipelineRunTableItem run) => PipelineRunTablePresentation.PipelineHref(run, ServerId);

    private string RunHref(PipelineRunTableItem run) => PipelineRunTablePresentation.RunHref(run, ServerId);
}
