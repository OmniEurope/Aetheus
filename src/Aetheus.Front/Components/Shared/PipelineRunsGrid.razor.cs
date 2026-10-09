// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

public partial class PipelineRunsGrid
{
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialogs { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<PipelineRunDto> Items { get; set; } = [];
    [Parameter] public bool GroupByProject { get; set; }
    [Parameter] public bool FillHeight { get; set; }
    [Parameter] public bool Virtualize { get; set; }
    [Parameter] public bool Compact { get; set; }
    [Parameter] public bool ShowDurationInCompact { get; set; }
    [Parameter] public bool IsLoading { get; set; }
    [Parameter] public int MaxGroups { get; set; } = int.MaxValue;
    [Parameter] public int? ServerId { get; set; }
    [Parameter] public string? CssClass { get; set; }
    [Parameter] public string EmptyTitleKey { get; set; } = "NoRunningPipelines";
    [Parameter] public string EmptyDescriptionKey { get; set; } = "NoRunningPipelinesHint";

    /// <summary>Hidden when the table already lives inside a single pipeline, where repeating its name
    /// on every row carries nothing.</summary>
    [Parameter] public bool ShowPipelineColumn { get; set; } = true;

    /// <summary>Raised after a run was cancelled, so the host can refresh its own data.</summary>
    [Parameter] public EventCallback OnRunCancelled { get; set; }

    private int? _cancelling;
    private IReadOnlyList<PipelineRunDto>? _groupedSource;
    private IReadOnlyList<PipelineRunTableItem> _displayItems = [];
    private int? _expandedRunId;
    private IReadOnlyList<object> _expandedKeys = [];
    private OmniDataGrid<PipelineRunTableItem>? _grid;
    private static readonly TimeSpan NewRowHighlight = TimeSpan.FromSeconds(5);

    // Recette R-210: header filter text, built once so the column sees the same delegate on every render.
    private Func<string, string>? _statusText;
    private Func<string, string> StatusText => _statusText ??= GridFilterText.ForEnum<PipelineStatus>(L);

    private IReadOnlyList<PipelineRunTableItem> DisplayItems => _displayItems;
    private bool UsesVirtualization => Virtualize && !DisplayItems.Any(run => run.LinkedRuns.Count > 0);
    private string GridCssClass =>
        $"pipeline-runs-grid aetheus-clickable-rows{(DisplayItems.Any(run => run.LinkedRuns.Count > 0) ? " pipeline-runs-grouped" : string.Empty)}{(Compact ? " pipeline-runs-compact" : string.Empty)}{(FillHeight ? " pipeline-grid-fill" : string.Empty)}{(UsesVirtualization ? " pipeline-grid-virtualized" : string.Empty)}{(string.IsNullOrWhiteSpace(CssClass) ? string.Empty : $" {CssClass}")}";

    protected override async Task OnParametersSetAsync()
    {
        if (ReferenceEquals(Items, _groupedSource))
            return;

        // Recette R-227: the host hands a new list on each live reload. The grid on screen notes the
        // runs it holds before it renders the new ones, so a run that just started reads bold for a
        // few seconds. The first list, or one replacing an empty table (no grid shown), marks nothing.
        if (_groupedSource is { Count: > 0 } && _grid is not null)
            await _grid.RefreshAsync();

        _groupedSource = Items;
        _displayItems = PipelineRunTableItem.GroupRuns(Items)
            .Take(Math.Max(0, MaxGroups))
            .ToList();

        if (_expandedRunId.HasValue)
        {
            var expandedRun = _displayItems.FirstOrDefault(run => run.RunId == _expandedRunId.Value);
            if (expandedRun is null || expandedRun.LinkedRuns.Count == 0)
                _expandedRunId = null;
        }
        _expandedKeys = _expandedRunId.HasValue ? [_expandedRunId.Value] : [];
    }

    private string PipelineHref(PipelineRunTableItem run) => PipelineRunTablePresentation.PipelineHref(run, ServerId);

    private string RunHref(PipelineRunTableItem run) => PipelineRunTablePresentation.RunHref(run, ServerId);

    private void OnRowClick(PipelineRunTableItem run) => Nav.NavigateTo(RunHref(run));

    private static void OnRowRender(OmniDataGridRowRenderArgs<PipelineRunTableItem> args) =>
        args.Expandable = args.Item.LinkedRuns.Count > 0;

    /// <summary>Only a run that is still going anywhere can be cancelled. A run whose cancellation was
    /// already accepted keeps running its mandatory teardown, so offering the button again would just
    /// repeat a request the backend has already durably taken.</summary>
    private static bool CanCancel(PipelineRunTableItem run) =>
        !run.CancellationRequested
        && run.Status is PipelineStatus.Pending or PipelineStatus.Running or PipelineStatus.WaitingForApproval;

    private async Task CancelRunAsync(PipelineRunTableItem run)
    {
        if (_cancelling.HasValue || !CanCancel(run))
            return;

        var confirmed = await Dialogs.Confirm(
            L["CancelRunConfirm"].Value,
            L["CancelRun"].Value,
            new OmniConfirmOptions
            {
                Destructive = true,
                ConfirmIcon = OmniIconName.Stop,
                OkButtonText = L["Stop"].Value,
                CancelButtonText = L["GoBack"].Value
            });
        if (confirmed != true)
            return;

        _cancelling = run.RunId;
        try
        {
            var status = await Api.Pipelines.CancelPipelineRunAsync(run.RunId);
            if (status.Success)
            {
                Toast.Info("RunCancelled", "RunCancelledDetail");
                await OnRunCancelled.InvokeAsync();
            }
            else if (status.Forbidden)
            {
                Toast.Error("PipelineRunFailed", "PipelineRunForbidden");
            }
            else
            {
                Toast.Error("PipelineRunFailed", "Error");
            }
        }
        finally
        {
            _cancelling = null;
        }
    }

    private static string? BranchHref(PipelineRunTableItem run) =>
        PipelineRunTablePresentation.BranchHref(run);

    private static string? CommitHref(PipelineRunTableItem run) =>
        PipelineRunTablePresentation.CommitHref(run);

    private void OnRowExpand(PipelineRunTableItem run) => _expandedRunId = run.RunId;

    private void OnRowCollapse(PipelineRunTableItem run)
    {
        if (_expandedRunId == run.RunId)
            _expandedRunId = null;
    }

    /// <summary>Recette R2-124: the export writes the duration the cell shows, a running run's up to now.</summary>
    private Func<PipelineRunTableItem, object?> DurationExport => _durationExport ??= run => PipelineRunTablePresentation.FormatDuration(run, L);
    private Func<PipelineRunTableItem, object?>? _durationExport;
}
