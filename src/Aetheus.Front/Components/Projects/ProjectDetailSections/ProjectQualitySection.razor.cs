// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

using Aetheus.Front.Components.Analysis;
public partial class ProjectQualitySection : ComponentBase, IAsyncDisposable
{
    private static readonly string[] QualitySectionSlugs = ["overview", "findings"];

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private ProjectDetailLoader Loader { get; set; } = default!;

    [Parameter] public int ProjectId { get; set; }

    /// <summary>The project header's menu outlet.</summary>
    [CascadingParameter] public Layout.ProjectSectionMenu? SectionMenu { get; set; }


    private bool _loading = true;
    private ProjectQualityTrendDto _trend = new();
    private List<CoveragePoint> _coverage = [];
    private List<TestPoint> _tests = [];
    private List<ComplexityPoint> _complexity = [];
    private IReadOnlyList<OmniChartPoint> TestPassedPoints => OmniChartData.Indexed(_tests, point => point.Passed, point => point.Label);
    private IReadOnlyList<OmniChartPoint> TestFailedPoints => OmniChartData.Indexed(_tests, point => point.Failed, point => point.Label);
    private IReadOnlyList<OmniChartPoint> ComplexityCrapPoints => OmniChartData.Indexed(_complexity, point => point.Crap, point => point.Label);
    private IReadOnlyList<OmniChartPoint> ComplexityAveragePoints => OmniChartData.Indexed(_complexity, point => point.AvgCc, point => point.Label);
    private AnalysisProjectSummaryDto _summary = new();
    private List<AnalysisFindingDto> _findings = [];
    private int _findingCount;
    private string? _findingSearch;
    private IReadOnlyList<AnalysisCategory> _findingCategories = [];
    private IReadOnlyList<AnalysisSeverity> _findingSeverities = [];
    private IReadOnlyList<AnalysisFindingStatus> _findingStatuses = [];
    private bool? _findingIsNew;
    private (int? From, int? To, int? Not) _findingIdRange;
    // What the findings grid opens filtered on, shown in its column headers (Status on Open by
    // default, or what a summary shortcut asked for) and removable there.
    private FindingColumnDefaults _findingDefaults = FindingColumnDefaults.OpenOnly;
    private Func<string, string>? _severityText;
    private Func<string, string>? _statusText;
    private Func<string, string>? _categoryText;
    private Func<string, string>? _yesNoText;
    private string? _findingScanner;
    private string? _findingBranch;
    private string? _findingResponsible;
    private AetheusDataGrid<AnalysisFindingDto>? _findingsGrid;
    private HubConnection? _realtime;
    private CancellationTokenSource? _reloadDebounce;
    private bool _disposed;
    private int? _loadedProjectId;
    private int _qualitySectionIndex;
    private FindingQuery? _lastFindingQueryKey;
    private string? _findingSortBy;
    private bool _findingSortDescending;
    private bool _exportingPrompt;

    private bool HasAny => _summary.Grade is not null
        || _coverage.Count > 0 || _tests.Count > 0 || _complexity.Count > 0;

    // A chart alone takes the full width instead of half of it beside an empty column.
    private int ChartLargeSpan => _tests.Count >= 2 && _complexity.Count >= 2 ? 6 : 12;

    /// <summary>A chart alone spans the card (wide and low), two share the row.</summary>
    private double ChartAspectRatio => ChartLargeSpan == 12 ? 4 : 2;

    private string LastAnalysisText => _summary.LastAnalysisAt?.ToString("g") ?? L["Never"];

    protected override async Task OnParametersSetAsync()
    {
        _qualitySectionIndex = QualitySectionIndexFromUri();

        // Query-only tab navigation must not replay the overview requests. The findings grid is
        // server-rendered only when its tab is active and performs its own single paged request.
        if (_loadedProjectId == ProjectId && !_loading) return;

        _loading = true;
        ResetProjectState();
        try
        {
            var trendTask = Api.Analysis.GetProjectQualityTrendAsync(ProjectId);
            var summaryTask = Api.Analysis.GetAnalysisSummaryAsync(ProjectId);
            await Task.WhenAll(trendTask, summaryTask);
            _trend = await trendTask;
            _summary = await summaryTask;
            if (_qualitySectionIndex == 1) await EnsureFindingsLoadedAsync();
        }
        catch (HttpRequestException)
        {
            ResetProjectState();
        }

        _coverage = _trend.Coverage
            .Select(c => new CoveragePoint($"#{c.RunId}", Math.Round(c.LineRate * 100, 1), Math.Round(c.BranchRate * 100, 1)))
            .ToList();
        _tests = _trend.Tests
            .Select(t => new TestPoint($"#{t.RunId}", t.Passed, t.Failed))
            .ToList();
        _complexity = _trend.Complexity
            .Where(c => c.CrapAvg.HasValue)
            .Select(c => new ComplexityPoint($"#{c.RunId}", Math.Round(c.CrapAvg!.Value, 1), Math.Round(c.AvgCyclomatic, 1)))
            .ToList();

        _loadedProjectId = ProjectId;
        _loading = false;
        if (_realtime is null && !_disposed) await StartRealtimeAsync();
    }

    private async Task OnQualitySectionChangedAsync(int index)
    {
        _qualitySectionIndex = Math.Clamp(index, 0, QualitySectionSlugs.Length - 1);
        if (_qualitySectionIndex == 1) await EnsureFindingsLoadedAsync();
        PublishMenuActions();
    }

    /// <summary>Findings export belongs beneath the table; the section publishes no separate header
    /// action.</summary>
    private void PublishMenuActions() => SectionMenu?.Clear();

    protected override void OnAfterRender(bool firstRender) => PublishMenuActions();

    private int QualitySectionIndexFromUri()
    {
        var query = QueryHelpers.ParseQuery(Navigation.ToAbsoluteUri(Navigation.Uri).Query);
        return query.TryGetValue("tab", out var tab)
            && string.Equals(tab.ToString(), QualitySectionSlugs[1], StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
    }

    /// <summary>Analysis-report completion arrives through the entities hub as a project
    /// change.</summary>
    private async Task StartRealtimeAsync()
    {
        try
        {
            _realtime = HubFactory.Create("entities");
            _realtime.On<ResourceType, int, string>("EntityChanged", (type, id, _) =>
                type == ResourceType.Project && id == ProjectId ? QueueRealtimeReloadAsync() : Task.CompletedTask);
            _realtime.RejoinOnReconnect(async () =>
            {
                await _realtime.InvokeAsync("JoinEntityUpdates", ResourceType.Project);
                await QueueRealtimeReloadAsync();
            });
            await _realtime.StartAsync();
            await _realtime.InvokeAsync("JoinEntityUpdates", ResourceType.Project);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException
            or Microsoft.AspNetCore.SignalR.HubException)
        {
            // Hub unavailable: the page keeps what it loaded.
        }
    }

    /// <summary>Debounce report bursts so a run's closely spaced scanner events produce one reload
    /// after they settle.</summary>
    internal static readonly TimeSpan RealtimeReloadQuietPeriod = TimeSpan.FromSeconds(5);

    private Task QueueRealtimeReloadAsync()
    {
        // Ignore late entity events once disposal starts to avoid using a disposed debounce source.
        if (_disposed) return Task.CompletedTask;
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
        _reloadDebounce = new CancellationTokenSource();
        var token = _reloadDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RealtimeReloadQuietPeriod, token);
                await InvokeAsync(async () =>
                {
                    _summary = await Api.Analysis.GetAnalysisSummaryAsync(ProjectId, token);
                    if (_qualitySectionIndex == 1 && _findingsGrid is not null)
                    {
                        _lastFindingQueryKey = null;
                        await _findingsGrid.Reload();
                    }
                    StateHasChanged();
                });
            }
            catch (OperationCanceledException) { }
        }, token);
        return Task.CompletedTask;
    }

    private Func<string, string> SeverityText => _severityText ??= GridFilterText.ForEnum<AnalysisSeverity>(L);
    private Func<string, string> StatusText => _statusText ??= GridFilterText.ForEnum<AnalysisFindingStatus>(L);
    private Func<string, string> CategoryText => _categoryText ??= GridFilterText.ForEnum<AnalysisCategory>(L);
    private Func<string, string> YesNoText => _yesNoText ??= GridFilterText.YesNo(L);

    private async Task LoadFindingsAsync(GridLoadArgs args)
    {
        // The column header filters, and only they, are the filters of the findings.
        ApplyHeaderFilters(args);
        var queryKey = FindingQueryKey(args);
        if (queryKey == _lastFindingQueryKey) return;

        var (page, size) = Page(args);
        var result = await FetchFindingsAsync(page, size, Sort(args), Descending(args));
        _findings = result.Items;
        _findingCount = result.TotalCount;
        _findingSortBy = Sort(args);
        _findingSortDescending = Descending(args);
        _lastFindingQueryKey = queryKey;
    }

    /// <summary> One page of the findings export, every page read from the API with the sort and
    /// the column filters of the grid. Those are the filters the grid's last load applied, so
    /// applying them again leaves the page's filters as they were; the rows on screen are not
    /// touched. </summary>
    private Task<PaginatedResult<AnalysisFindingDto>> ReadFindingsAsync(GridLoadArgs args)
    {
        ApplyHeaderFilters(args);
        var (page, size) = Page(args);
        return FetchFindingsAsync(page, size, Sort(args), Descending(args));
    }

    private void ApplyHeaderFilters(GridLoadArgs args)
    {
        _findingSearch = args.ColumnFilter(nameof(AnalysisFindingDto.Title)) ?? args.ColumnFilter(nameof(AnalysisFindingDto.RuleId));
        _findingCategories = args.ColumnFilterValues<AnalysisCategory>(nameof(AnalysisFindingDto.Category));
        _findingSeverities = args.ColumnFilterValues<AnalysisSeverity>(nameof(AnalysisFindingDto.Severity));
        _findingStatuses = args.ColumnFilterValues<AnalysisFindingStatus>(nameof(AnalysisFindingDto.Status));
        _findingResponsible = args.ColumnFilter(nameof(AnalysisFindingDto.Responsible));
        _findingIsNew = bool.TryParse(args.ColumnFilter(FindingColumnDefaults.IsNewColumn), out var isNew) ? isNew : null;
        _findingIdRange = args.ColumnWholeNumberRange(nameof(AnalysisFindingDto.Id));
    }

    /// <summary>One value of a column travels in the historical single parameter, several in the list
    /// one, so a link or a client written for the single parameter keeps working.</summary>
    private Task<PaginatedResult<AnalysisFindingDto>> FetchFindingsAsync(int page, int size, string? sortBy, bool sortDescending) =>
        Api.Analysis.GetAnalysisFindingsAsync(ProjectId, page, size, _findingSearch,
            Single(_findingCategories), Single(_findingSeverities), Single(_findingStatuses), _findingIsNew,
            _findingScanner, _findingBranch, _findingResponsible, sortBy: sortBy, sortDescending: sortDescending,
            categories: Several(_findingCategories), severities: Several(_findingSeverities), statuses: Several(_findingStatuses),
            idFrom: _findingIdRange.From, idTo: _findingIdRange.To, idNot: _findingIdRange.Not);

    private static T? Single<T>(IReadOnlyList<T> values) where T : struct => values.Count == 1 ? values[0] : null;
    private static IReadOnlyList<T>? Several<T>(IReadOnlyList<T> values) => values.Count > 1 ? values : null;

    /// <summary>The first page before the grid asks, filtered on what its columns open with, so the
    /// grid's own first request is the same query and is answered from it.</summary>
    private Task EnsureFindingsLoadedAsync() => _lastFindingQueryKey is null
        ? LoadFindingsAsync(new GridLoadArgs { Skip = 0, Top = 25, Filters = _findingDefaults.AsFilters() })
        : Task.CompletedTask;

    private void OpenFinding(AnalysisFindingDto item)
    {
        Navigation.NavigateTo($"/analysis/findings/{item.Id}");
    }

    private async Task OpenFindingsAsync(
        AnalysisFindingStatus? status = null,
        AnalysisSeverity? severity = null,
        bool? isNew = null)
    {
        // A shortcut sets the column filters themselves, visible and removable in the headers. The
        // grid is not on screen from the overview, so its columns open with them.
        _findingDefaults = new FindingColumnDefaults(status, severity, isNew);
        _findingScanner = null;
        _findingBranch = null;
        _lastFindingQueryKey = null;
        if (_findingsGrid?.Grid is { } grid)
        {
            await grid.SetFiltersAsync(_findingDefaults.AsColumnValues(), replace: true);
            return;
        }

        SelectQualitySection(1);
        await EnsureFindingsLoadedAsync();
    }

    private void SelectQualitySection(int index)
    {
        _qualitySectionIndex = Math.Clamp(index, 0, QualitySectionSlugs.Length - 1);
        var slug = _qualitySectionIndex == 0 ? null : QualitySectionSlugs[_qualitySectionIndex];
        Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("tab", slug));
    }

    private static string SectionText(string label, int count) =>
        count > 0 ? $"{label} · {count:N0}" : label;

    private async Task ExportAllAiPromptAsync()
    {
        if (_exportingPrompt || _findingCount == 0) return;

        _exportingPrompt = true;
        PublishMenuActions();
        try
        {
            const int pageSize = 200;
            var page = 1;
            var findings = new List<AnalysisFindingDto>(_findingCount);
            while (findings.Count < _findingCount)
            {
                var result = await FetchFindingsAsync(page, pageSize, _findingSortBy, _findingSortDescending);
                findings.AddRange(result.Items);
                if (result.Items.Count == 0 || findings.Count >= result.TotalCount) break;
                page++;
            }

            var markdown = AnalysisAiPromptBuilder.BuildBulk(findings, L);
            await Js.InvokeVoidAsync(
                "downloadFile",
                // "<project>-findings-2026-10-01.md"; the loader holds the open project.
                ExportFileNames.Dated(Loader.Project is { } project && project.Id == ProjectId ? project.Name : null,
                    $"project-{ProjectId}", "findings", DateTime.Now, "md"),
                markdown,
                "text/markdown");
        }
        finally
        {
            _exportingPrompt = false;
            PublishMenuActions();
        }
    }

    private static async Task ReloadFromFirstPageAsync<T>(AetheusDataGrid<T>? grid)
        where T : notnull
    {
        if (grid is null) return;
        await grid.GoToPage(0);
        await grid.Reload();
    }

    private void ResetProjectState()
    {
        _trend = new();
        _summary = new();
        _findings = [];
        _findingCount = 0;
        _findingDefaults = FindingColumnDefaults.OpenOnly;
        _findingSortBy = null;
        _findingSortDescending = false;
        _exportingPrompt = false;
        _lastFindingQueryKey = null;
    }

    private static (int Page, int Size) Page(GridLoadArgs args)
    {
        var size = Math.Clamp(args.Top ?? 25, 1, 200);
        return (((args.Skip ?? 0) / size) + 1, size);
    }

    private static string? Sort(GridLoadArgs args) => args.Sorts?.FirstOrDefault()?.Property;
    private static bool Descending(GridLoadArgs args) =>
        args.Sorts?.FirstOrDefault()?.SortOrder == GridSortOrder.Descending;

    private FindingQuery FindingQueryKey(GridLoadArgs args)
    {
        var (page, size) = Page(args);
        return new FindingQuery(
            page,
            size,
            _findingSearch,
            string.Join(',', _findingCategories),
            string.Join(',', _findingSeverities),
            string.Join(',', _findingStatuses),
            _findingIsNew,
            _findingScanner,
            _findingBranch,
            _findingResponsible,
            Sort(args),
            Descending(args),
            _findingIdRange.From,
            _findingIdRange.To,
            _findingIdRange.Not);
    }

    /// <summary>The recorded commit's page; the commit page finds its repository. Null (plain text)
    /// when the project's history does not hold the commit.</summary>
    private string? GradeCommitHref => _summary.GradeCommitId is { } commitId
        ? $"/git-repositories/commits/{commitId}"
        : null;

    private static string? ShortHash(string? hash) =>
        string.IsNullOrWhiteSpace(hash) ? null : hash.Length > 8 ? hash[..8] : hash;

    private sealed record CoveragePoint(string Label, double LinePct, double BranchPct);
    private sealed record TestPoint(string Label, int Passed, int Failed);
    private sealed record ComplexityPoint(string Label, double Crap, double AvgCc);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        SectionMenu?.Clear();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
        _reloadDebounce = null;
        if (_realtime is not null) await _realtime.DisposeAsync();
    }
}
