// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

public partial class ProjectArtifactsSection : IAsyncDisposable
{
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    private HubConnection? _pipelinesHub;

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    private List<PipelineArtifactDto> _artifacts = [];
    private int? _loadedProjectId;
    private int _totalCount;
    private int _pageSize = 25;
    private string _sortBy = "CreatedAt";
    private bool _sortDescending = true;
    private bool _loading = true;
    private ArtifactRetentionPolicy? _selectedPolicy;
    private AetheusDataGrid<PipelineArtifactDto>? _grid;

    // The grid's header filters travel with every page request, the pipeline and environment lists
    // offer the names present across the project's artifacts.
    private List<Aetheus.Shared.Components.Shared.GridFilter> _columnFilters = [];
    private ProjectArtifactFilterValuesDto _filterValues = new();
    private Func<string, string>? _policyText;
    private Func<string, string> PolicyText => _policyText ??= GridFilterText.ForEnum<ArtifactRetentionPolicy>(L);

    private readonly List<PolicyOption> _policyOptions =
    [
        new("Build", ArtifactRetentionPolicy.Build),
        new("Deployed", ArtifactRetentionPolicy.Deployed),
        new("Released", ArtifactRetentionPolicy.Released)
    ];

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedProjectId == ProjectId) return;
        _loadedProjectId = ProjectId;
        // Stale-while-revalidate: seed from cache (and drop the section spinner) so a revisit paints
        // the previous rows instantly; LoadDataAsync then revalidates in the background.
        Cache.Seed<PaginatedResult<PipelineArtifactDto>>(
            CacheKey(1, _pageSize, _selectedPolicy, _columnFilters, _sortBy, _sortDescending),
            result => { ApplyArtifacts(result); _loading = false; });
        try
        {
            var existingGrid = _grid;
            await LoadFilterValuesAsync();
            if (existingGrid is not null) await existingGrid.Reload();
        }
        catch (HttpRequestException) { } // 401 on expired JWT - redirect handled by AuthProvider
    }

    private async Task LoadFilterValuesAsync()
    {
        var projectId = ProjectId;
        var values = await Api.ServerTools.GetProjectArtifactFilterValuesAsync(projectId);
        if (ProjectId == projectId) _filterValues = values;
    }

    /// <summary> Artifacts are produced by pipeline runs, so a finished run reloads the list; the
    /// Refresh button is gone. Best effort like the other realtime lists: without the hub the page
    /// still shows what it loaded. </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try
        {
            _pipelinesHub = HubFactory.Create("pipelines");
            _pipelinesHub.On<int, PipelineStatus>("PipelineRunCompleted", (_, _) => InvokeAsync(RefreshAsync));
            await _pipelinesHub.StartAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // Hub unavailable: the section works without live updates (same stance as ReleasesList).
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipelinesHub is not null)
            await _pipelinesHub.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>A finished run refreshes the grid quietly (rows, page, filters and scroll kept) and
    /// brings the names of a new pipeline or environment into the filters. the grid is always
    /// rendered and scrolls (remote virtualization): it only shows what it fetched
    /// itself.</summary>
    private async Task RefreshAsync()
    {
        await (_grid is not null ? _grid.Refresh() : LoadDataAsync());
        await LoadFilterValuesAsync();
        StateHasChanged();
    }

    /// <summary>The type drop-down narrows from the first page, through the grid so its header
    /// filters and sort stay applied.</summary>
    private Task OnPolicyChangedAsync() =>
        _grid is not null ? _grid.GoToPage(0, forceReload: true) : LoadDataAsync();

    private string CacheKey(int page, int pageSize, ArtifactRetentionPolicy? policy,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter> filters,
        string sortBy, bool sortDescending) =>
        $"artifacts:{ProjectId}:{page}:{pageSize}:{policy}:{GridColumnFilters.CacheText(filters)}:{sortBy}:{sortDescending}";

    private void ApplyArtifacts(PaginatedResult<PipelineArtifactDto> result)
    {
        _artifacts = result.Items;
        _totalCount = result.TotalCount;
    }

    // Section-level (re)load: drives the full-section spinner via _loading. Used for the first load
    // and while the grid is not rendered; a cache hit skips the spinner, a cold view shows it. It keeps
    // the grid's last header filters so the rows always match what the headers say.
    private Task LoadDataAsync()
    {
        var projectId = ProjectId;
        var filters = _columnFilters;
        var sortBy = _sortBy;
        var sortDescending = _sortDescending;
        return Cache.RevalidateAsync(
            CacheKey(1, _pageSize, _selectedPolicy, filters, sortBy, sortDescending),
            () => Api.ServerTools.GetProjectArtifactsAsync(projectId, 1, _pageSize, _selectedPolicy,
                filters: filters, sortBy: sortBy, sortDescending: sortDescending),
            result => { if (ProjectId == projectId) ApplyArtifacts(result); },
            loading => { if (ProjectId == projectId) _loading = loading; },
            () => InvokeAsync(StateHasChanged));
    }

    // The grid owns loading; its headers stay mounted while the rows arrive.
    /// <summary>One page of the export, with the retention filter, sort and column filters of the
    /// grid.</summary>
    private Task<PaginatedResult<PipelineArtifactDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest("CreatedAt", fallbackDescending: true);
        return Api.ServerTools.GetProjectArtifactsAsync(ProjectId, page, pageSize, _selectedPolicy,
            filters: args.ToApiFilters(), sortBy: sortBy, sortDescending: sortDescending);
    }

    private Task OnLoadData(GridLoadArgs args)
    {
        var page = (args.Skip ?? 0) / _pageSize + 1;
        var projectId = ProjectId;
        var filters = _columnFilters = args.ToApiFilters();
        (_sortBy, _sortDescending) = args.ToSortRequest("CreatedAt", fallbackDescending: true);
        var sortBy = _sortBy;
        var sortDescending = _sortDescending;
        return Cache.RevalidateAsync(
            CacheKey(page, _pageSize, _selectedPolicy, filters, sortBy, sortDescending),
            () => Api.ServerTools.GetProjectArtifactsAsync(projectId, page, _pageSize, _selectedPolicy,
                filters: filters, sortBy: sortBy, sortDescending: sortDescending),
            result => { if (ProjectId == projectId) ApplyArtifacts(result); },
            loading => { if (ProjectId == projectId) _loading = loading; },
            () => InvokeAsync(StateHasChanged));
    }

    private void OnRowClick(PipelineArtifactDto artifact)
    {
        Nav.NavigateTo($"/artifacts/{artifact.Id}");
    }

    private async Task DownloadArtifact(PipelineArtifactDto artifact)
    {
        var stream = await Api.ServerTools.DownloadArtifactAsync(artifact.Id);
        if (stream is null) return;

        using var streamRef = new DotNetStreamReference(stream);
        await Js.InvokeVoidAsync("downloadFileFromStream", $"{artifact.Name}.zip", streamRef);
    }

    private static string FormatSize(long bytes) => DisplayFormatting.Size(bytes);

    private static OmniTone GetPolicyBadge(ArtifactRetentionPolicy policy) => policy switch
    {
        ArtifactRetentionPolicy.Build => OmniTone.Accent,
        ArtifactRetentionPolicy.Deployed => OmniTone.Warning,
        ArtifactRetentionPolicy.Released => OmniTone.Success,
        _ => OmniTone.Neutral
    };

    private sealed record PolicyOption(string Label, ArtifactRetentionPolicy Value);

    /// <summary>The export writes the version of every release the artifact is in.</summary>
    private static readonly Func<PipelineArtifactDto, object?> ReleaseExport = artifact => string.Join(", ", artifact.Releases.Select(release => release.Version));
}
