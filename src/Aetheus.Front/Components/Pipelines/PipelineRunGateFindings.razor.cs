// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

using Aetheus.Front.Components.Analysis;
public partial class PipelineRunGateFindings : IDisposable
{
    /// <summary>The export reads the listed findings page after page, up to this many.</summary>
    internal const int ExportLimit = 2_000;
    private const int ExportPageSize = 200;

    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Parameter, EditorRequired] public AnalysisRunGateDto Gate { get; set; } = default!;

    /// <summary>The runs whose findings the tab lists (the run and the runs it triggered), read
    /// from the server page by page. Empty means the gate's own run.</summary>
    [Parameter] public IReadOnlyList<int> RunIds { get; set; } = [];

    [Parameter] public int? ProjectId { get; set; }

    /// <summary>The run's project, the start of the exported prompt's file name.</summary>
    [Parameter] public string? ProjectName { get; set; }

    /// <summary>Raised with the finding whose decision was reverted, open again.</summary>
    [Parameter] public EventCallback<int> FindingReopened { get; set; }

    /// <summary>The letter the run is listed with; when the assurance seal set it, the tab says so
    /// and gives the analysis letter beside it.</summary>
    [Parameter] public AnalysisGrade? RunGrade { get; set; }

    /// <summary>The tests the seal held the candidate back for, each traced to its producing step when known.</summary>
    [Parameter] public IReadOnlyList<PipelineRunAssuranceVerdict.BlockingTest> BlockingTests { get; set; } = [];

    private AetheusDataGrid<AnalysisFindingDto>? _grid;
    private IReadOnlyList<AnalysisFindingDto> _rows = [];
    private int _total;
    private AnalysisRunFindingsRequest? _lastRequest;
    private bool _exporting;

    // Deciding on a finding, and so reverting a decision, takes the project administration permission.
    private bool CanDecide => ProjectId is { } projectId && Permissions.CanAdmin(ResourceType.Project, projectId);

    protected override void OnInitialized() => Permissions.OnPermissionsChanged += PermissionsChanged;

    private void PermissionsChanged() => _ = InvokeAsync(StateHasChanged);

    public void Dispose() => Permissions.OnPermissionsChanged -= PermissionsChanged;

    // A finding someone accepted, mitigated or ruled a false positive is not left to do.
    private bool _showDecided;

    /// <summary>How many findings the list holds with the decided ones shown or not, from the gate's counts.</summary>
    private int ListedCount => _showDecided ? Gate.FindingCount + Gate.DecidedFindingCount : Gate.FindingCount;

    /// <summary>Whether the grade block draws anything (it needs a grade or a headline grade). Without it the
    /// two key-figure tiles sit in a row at tile size instead of a column stretched over the whole width.</summary>
    private bool HasGradeBlock => Gate.Grade is not null || PipelineRunGatePresentation.HeadlineGrade(Gate, RunGrade) is not null;

    private IReadOnlyList<int> EffectiveRunIds => RunIds.Count > 0 ? RunIds : [Gate.PipelineRunId];

    private async Task ShowDecidedChangedAsync()
    {
        _lastRequest = null;
        if (_grid is not null) await _grid.Reload();
    }

    /// <summary>One page of the tree's findings, filtered and sorted by the server as the column
    /// headers ask.</summary>
    private async Task LoadAsync(GridLoadArgs args)
    {
        var request = RequestOf(args);
        var page = await Api.Analysis.GetAnalysisRunFindingsAsync(request);
        _rows = [.. page.Items.Select(AnalysisFindingColumns.ToRow)];
        _total = page.TotalCount;
        _lastRequest = request;
    }

    /// <summary>One page of the export, the same query as the grid's load.</summary>
    private async Task<PaginatedResult<AnalysisFindingDto>> ReadExportAsync(GridLoadArgs args)
    {
        var page = await Api.Analysis.GetAnalysisRunFindingsAsync(RequestOf(args));
        return new PaginatedResult<AnalysisFindingDto>
        {
            Items = [.. page.Items.Select(AnalysisFindingColumns.ToRow)],
            TotalCount = page.TotalCount
        };
    }

    /// <summary>The findings of the run's analyses the grid asks for: its page, column filters and sort.</summary>
    private AnalysisRunFindingsRequest RequestOf(GridLoadArgs args)
    {
        var size = Math.Clamp(args.Top ?? 25, 1, PaginationRequest.MaxPageSize);
        var sort = args.Sorts?.FirstOrDefault();
        var (idFrom, idTo, idNot) = args.ColumnWholeNumberRange(nameof(AnalysisFindingDto.Id));
        return new AnalysisRunFindingsRequest
        {
            RunIds = [.. EffectiveRunIds],
            IncludeDecided = _showDecided,
            Page = ((args.Skip ?? 0) / size) + 1,
            PageSize = size,
            Search = args.ColumnFilter(nameof(AnalysisFindingDto.Title)) ?? args.ColumnFilter(nameof(AnalysisFindingDto.RuleId)),
            Categories = NullIfEmpty(args.ColumnFilterValues<AnalysisCategory>(nameof(AnalysisFindingDto.Category))),
            Severities = NullIfEmpty(args.ColumnFilterValues<AnalysisSeverity>(nameof(AnalysisFindingDto.Severity))),
            Statuses = NullIfEmpty(args.ColumnFilterValues<AnalysisFindingStatus>(nameof(AnalysisFindingDto.Status))),
            IsNew = bool.TryParse(args.ColumnFilter(FindingColumnDefaults.IsNewColumn), out var isNew) ? isNew : null,
            IdFrom = idFrom,
            IdTo = idTo,
            IdNot = idNot,
            SortBy = sort?.Property,
            SortDescending = sort?.SortOrder == GridSortOrder.Descending
        };
    }

    private static List<T>? NullIfEmpty<T>(IReadOnlyList<T> values) => values.Count > 0 ? [.. values] : null;

    private async Task RevertDecisionAsync(AnalysisFindingDto finding)
    {
        if (!await new AnalysisFindingDecisionRevert(Api, Dialog, Toast, L).RevertAsync(finding.Id)) return;
        await FindingReopened.InvokeAsync(finding.Id);
        if (_grid is not null) await _grid.Reload();
    }

    private void OpenFinding(AnalysisFindingDto finding) => Navigation.NavigateTo(AnalysisFindingColumns.FindingHref(finding.Id, Gate.PipelineRunId));
    private void OpenProjectQuality(int projectId) => Navigation.NavigateTo($"/projects/{projectId}/quality");

    /// <summary>The export previews the grid-filtered findings before download, reading successive
    /// pages up to <see cref="ExportLimit"/>.</summary>
    private async Task ExportAllAiPromptAsync()
    {
        _exporting = true;
        try
        {
            var findings = await ReadListedFindingsAsync();
            await Dialog.OpenAsync<TextExportDialog>(
                L["AnalysisExport"],
                new Dictionary<string, object?>
                {
                    [nameof(TextExportDialog.Text)] = AnalysisAiPromptBuilder.BuildBulk(findings, L),
                    // "aetheus-run-2478-findings-2026-10-01.md".
                    [nameof(TextExportDialog.FileName)] = ExportFileNames.Dated(
                        ProjectName, "project", $"run-{Gate.PipelineRunId}-findings", DateTime.Now, "md"),
                    [nameof(TextExportDialog.ContentType)] = "text/markdown"
                },
                new OmniDialogOptions { Width = "min(52rem, 96vw)", CloseDialogOnEsc = true, ShowClose = true, AutoFocusFirstElement = false });
        }
        catch (HttpRequestException)
        {
            Toast.Error("AnalysisExport", "GridLoadFailed");
        }
        finally
        {
            _exporting = false;
        }
    }

    private async Task<List<AnalysisRunGateFindingDto>> ReadListedFindingsAsync()
    {
        var filter = (_lastRequest ?? new AnalysisRunFindingsRequest { RunIds = [.. EffectiveRunIds], IncludeDecided = _showDecided })
            with
        { PageSize = ExportPageSize };
        var findings = new List<AnalysisRunGateFindingDto>();
        for (var page = 1; findings.Count < ExportLimit; page++)
        {
            var result = await Api.Analysis.GetAnalysisRunFindingsAsync(filter with { Page = page });
            findings.AddRange(result.Items);
            if (result.Items.Count < ExportPageSize || findings.Count >= result.TotalCount) break;
        }
        return findings.Count > ExportLimit ? [.. findings.Take(ExportLimit)] : findings;
    }
}
