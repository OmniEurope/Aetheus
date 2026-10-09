// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineFleet
{
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    private readonly TrailingReloadCoalescer _liveReload = new(1000);

    private AetheusDataGrid<PipelineFleetItemDto>? _grid;
    private List<PipelineFleetItemDto> _items = [];
    private List<ProjectDto> _projects = [];
    // Recette R-224: the grid's header filters, sent with every page request, and the values they offer.
    private List<GridFilter> _columnFilters = [];
    private PipelineFleetFilterValuesDto _filterValues = new();
    private Func<string, string>? _freshnessFilterText;
    private Func<string, string> FreshnessFilterText => _freshnessFilterText ??= value =>
        Enum.TryParse<PipelineFleetFreshness>(value, out var freshness) ? FreshnessText(freshness) : value;
    private int _totalCount;
    private bool _loading;
    private bool _filtersLoadError;
    private bool _gridLoadError;
    private int? _projectId;
    private int _page = 1;
    private int _pageSize = 25;
    private string? _sortBy;
    private bool _sortDescending;
    private readonly object _permissionRefreshLock = new();
    private Task _permissionRefreshTask = Task.CompletedTask;

    protected override async Task OnInitializedAsync()
    {
        Breadcrumb.Set(new BreadcrumbItem(L["PipelineFleet"]));
        FollowPermissions();
        try
        {
            var filterValuesTask = Api.Pipelines.GetPipelineFleetFilterValuesAsync();
            var projectsTask = Api.Projects.GetAllProjectsAsync();
            await Task.WhenAll(filterValuesTask, projectsTask);
            _filterValues = await filterValuesTask;
            _projects = await projectsTask;
        }
        catch (HttpRequestException)
        {
            _filtersLoadError = true;
        }
        // Recette R-181: the fleet is the pipelines and the model versions they run, so a change to a
        // pipeline or to a model reloads the current page in place, without the Refresh button.
        await FollowEntitiesAsync(ResourceType.Pipeline, ResourceType.PipelineTemplate);
    }

    protected override Task OnEntitiesChangedAsync() => _liveReload.RequestAsync(RefreshAsync);

    /// <summary>Recette R-226: a live change fetches the current page again through the grid, rows kept on
    /// screen, page, sort and filters unchanged.</summary>
    private Task RefreshAsync() => _grid is not null ? _grid.Refresh() : LoadAsync();

    internal async Task LoadDataAsync(GridLoadArgs args)
    {
        _pageSize = args.Top ?? 25;
        _page = ((args.Skip ?? 0) / _pageSize) + 1;
        (_sortBy, _sortDescending) = ResolveSort(args);
        _columnFilters = args.ToApiFilters();
        await LoadAsync();
    }

    /// <summary>Recette R2-124: one page of the export, with the project filter, sort and column filters of the grid.</summary>
    internal Task<PaginatedResult<PipelineFleetItemDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = ResolveSort(args);
        return Api.Pipelines.GetPipelineFleetAsync(page, pageSize, projectId: _projectId,
            sortBy: sortBy, sortDescending: sortDescending, filters: args.ToApiFilters());
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _gridLoadError = false;
        try
        {
            var result = await Api.Pipelines.GetPipelineFleetAsync(
                _page, _pageSize, projectId: _projectId,
                sortBy: _sortBy, sortDescending: _sortDescending, filters: _columnFilters);
            _items = result.Items;
            _totalCount = result.TotalCount;
        }
        catch (HttpRequestException)
        {
            _items = [];
            _totalCount = 0;
            _gridLoadError = true;
        }
        finally
        {
            _loading = false;
        }
    }

    private static (string? SortBy, bool Descending) ResolveSort(GridLoadArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy)) return (null, false);
        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allowed = parts[0] is "PipelineName" or "OwnerName" or "TemplateName"
            or "PinnedVersion" or "LatestVersion" or "Freshness";
        return allowed
            ? (parts[0], parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase))
            : (null, false);
    }

    private Task OnProjectChanged(int? value)
    {
        _projectId = value;
        return ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _page = 1;
        // Recette R-327: the grid scrolls (remote virtualization) and only shows what it fetched itself, so
        // another project reloads it from its first row through its own loader.
        if (_grid is not null)
            await _grid.GoToPage(0, forceReload: true);
        else
            await LoadAsync();
    }

    private void OpenUpdateAsync(PipelineFleetItemDto item) =>
        Nav.NavigateTo($"/pipelines/{item.PipelineId}/template/update/{item.LatestVersion!.Value}");

    private bool CanUpdate(PipelineFleetItemDto item) =>
        Permissions.CanWrite(ResourceType.Pipeline, item.PipelineId);

    private string FreshnessText(PipelineFleetFreshness freshness) => freshness switch
    {
        PipelineFleetFreshness.Current => L["Current"],
        PipelineFleetFreshness.Outdated => L["Outdated"],
        _ => L["OffCatalog"]
    };

    private static OmniTone FreshnessStyle(PipelineFleetFreshness freshness) => freshness switch
    {
        PipelineFleetFreshness.Current => OmniTone.Success,
        PipelineFleetFreshness.Outdated => OmniTone.Warning,
        _ => OmniTone.Neutral
    };

    protected override void OnPermissionsChanged()
    {
        lock (_permissionRefreshLock)
            _permissionRefreshTask = RefreshPermissionsAsync(_permissionRefreshTask);
    }

    private async Task RefreshPermissionsAsync(Task previousRefresh)
    {
        await previousRefresh;
        await InvokeAsync(StateHasChanged);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Task pendingRefresh;
        lock (_permissionRefreshLock)
            pendingRefresh = _permissionRefreshTask;
        await pendingRefresh;
    }
}
