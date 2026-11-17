// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Shared;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Recette R-24: the table of what runs right now, shared by the general, project and server pipeline lists.
/// It draws the live runs its host already holds; it loads nothing and subscribes to nothing.</summary>
public partial class PipelinesRunningNow
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private GridExportContext ExportContext { get; set; } = default!;

    /// <summary>The runs the list follows live; the table keeps the unfinished ones.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<PipelineRunDto> Runs { get; set; } = [];

    /// <summary>True on the general list, where the project of each run is told.</summary>
    [Parameter] public bool GroupByProject { get; set; }

    /// <summary>The server the list is scoped to; kept in the link so the run opens inside that server.</summary>
    [Parameter] public int? ServerId { get; set; }

    private IReadOnlyList<PipelineRunTableItem> Rows => PipelineRunningNowRows.From(Runs);

    private string RunHref(PipelineRunTableItem run) => PipelineRunTablePresentation.RunHref(run, ServerId);

    private Func<string, string>? _statusText;
    private Func<string, string> StatusText => _statusText ??= GridFilterText.ForEnum<PipelineStatus>(L);

    private string DurationText(PipelineRunTableItem run) => PipelineRunTablePresentation.FormatDuration(run);
}
