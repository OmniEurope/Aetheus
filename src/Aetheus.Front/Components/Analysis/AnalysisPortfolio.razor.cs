// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Analysis;

public partial class AnalysisPortfolio : ComponentBase, IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;

    private AetheusDataGrid<AnalysisPortfolioRowDto>? _grid;
    private List<AnalysisPortfolioRowDto> _rows = [];
    private List<AnalysisPortfolioProjectDto> _projectSummaries = [];
    private int _count;
    private HubConnection? _hubConnection;

    // Recette R-224: the column header filters, and only they, filter the grid (the filter bar is gone).
    private List<GridFilter> _columnFilters = [];
    private AnalysisPortfolioFilterValuesDto _filterValues = new();
    private Func<string, string>? _categoryText;
    private Func<string, string>? _gateText;
    private Func<string, string>? _gradeText;
    private Func<string, string> CategoryText => _categoryText ??= GridFilterText.ForEnum<AnalysisCategory>(L);
    private Func<string, string> GateText => _gateText ??= GridFilterText.ForEnum<AnalysisGateStatus>(L);
    private Func<string, string> GradeText => _gradeText ??= GridFilterText.ForEnum<AnalysisGrade>(L);

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var summariesTask = Api.Analysis.GetAnalysisPortfolioProjectsAsync();
            var filterValuesTask = Api.Analysis.GetAnalysisPortfolioFilterValuesAsync();
            await Task.WhenAll(summariesTask, filterValuesTask);
            _projectSummaries = await summariesTask;
            _filterValues = await filterValuesTask;
        }
        catch (HttpRequestException)
        {
            _projectSummaries = [];
            _filterValues = new AnalysisPortfolioFilterValuesDto();
        }

        try
        {
            // The grid does not invoke LoadData on every first render (notably when the
            // surrounding async content changes). Prime the first page explicitly so
            // the portfolio never shows populated project summaries above an empty grid.
            await LoadDataAsync(new GridLoadArgs { Skip = 0, Top = 25 });
        }
        catch (HttpRequestException)
        {
            _rows = [];
            _count = 0;
        }

        await StartRealtimeAsync();
    }

    /// <summary>Recette R2-124: one page of the export, with the grid's sort and column filters.</summary>
    internal Task<PaginatedResult<AnalysisPortfolioRowDto>> ReadExportAsync(GridLoadArgs args)
    {
        var size = Math.Clamp(args.Top ?? 25, 1, 200);
        var page = ((args.Skip ?? 0) / size) + 1;
        var sort = args.Sorts?.FirstOrDefault();
        return Api.Analysis.GetAnalysisPortfolioAsync(page, size, sortBy: sort?.Property,
            sortDescending: sort?.SortOrder == GridSortOrder.Descending, filters: args.ToApiFilters());
    }

    internal async Task LoadDataAsync(GridLoadArgs args)
    {
        var size = Math.Clamp(args.Top ?? 25, 1, 200);
        var page = ((args.Skip ?? 0) / size) + 1;
        var sort = args.Sorts?.FirstOrDefault();
        _columnFilters = args.ToApiFilters();
        var result = await Api.Analysis.GetAnalysisPortfolioAsync(page, size, sortBy: sort?.Property,
            sortDescending: sort?.SortOrder == GridSortOrder.Descending, filters: _columnFilters);
        _rows = result.Items;
        _count = result.TotalCount;
    }

    // Recette R-226: a live change fetches the current page again in place, page, sort and filters kept.
    private Task RefreshAsync() => _grid?.Refresh() ?? Task.CompletedTask;

    private async Task StartRealtimeAsync()
    {
        try
        {
            _hubConnection = HubFactory.Create("entities");
            _hubConnection.On<ResourceType, int, string>("EntityChanged", (type, _, _) =>
                type == ResourceType.Project
                    ? InvokeAsync(RefreshAsync)
                    : Task.CompletedTask);
            _hubConnection.RejoinOnReconnect(() => InvokeAsync(async () =>
            {
                await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.Project);
                await RefreshAsync();
            }));
            await _hubConnection.StartAsync();
            await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.Project);
        }
        catch { /* SignalR is best-effort; filters remain manually reloadable. */ }
    }

    private void OpenRun(AnalysisPortfolioRowDto row)
    {
        if (row.PipelineRunId.HasValue) Navigation.NavigateTo($"/pipelines/runs/{row.PipelineRunId}");
    }

    private string Posture(AnalysisCategory category) => category is AnalysisCategory.Sast
        or AnalysisCategory.Secrets or AnalysisCategory.Dependencies or AnalysisCategory.Container
        or AnalysisCategory.InfrastructureAsCode or AnalysisCategory.Sbom or AnalysisCategory.Dast
        ? L["Security"] : L["Quality"];

    private static OmniTone PostureStyle(AnalysisCategory category) => category is AnalysisCategory.Sast
        or AnalysisCategory.Secrets or AnalysisCategory.Dependencies or AnalysisCategory.Container
        or AnalysisCategory.InfrastructureAsCode or AnalysisCategory.Sbom or AnalysisCategory.Dast
        ? OmniTone.Danger : OmniTone.Accent;

    private static OmniTone GateStyle(AnalysisGateStatus? status) => status switch
    {
        AnalysisGateStatus.Passed => OmniTone.Success,
        AnalysisGateStatus.Warning => OmniTone.Warning,
        AnalysisGateStatus.Blocked or AnalysisGateStatus.Error => OmniTone.Danger,
        _ => OmniTone.Neutral
    };

    /// <summary>Recette R-373: the commit's page in its internal repository when the backend could tell
    /// the repository from the report's run; text (null) otherwise, never a list to search in.</summary>
    internal static string? CommitHref(AnalysisPortfolioRowDto row) =>
        row.RepositoryId is { } repositoryId && !string.IsNullOrWhiteSpace(row.CommitHash)
            ? $"/git-repositories/{repositoryId}/commits/{Uri.EscapeDataString(row.CommitHash)}"
            : null;

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is null) return;
        await _hubConnection.LeaveEntityUpdatesAndDisposeAsync(ResourceType.Project);
        _hubConnection = null;
    }
}
