// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerProjectsSection
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    private List<ProjectDto> _projects = [];
    private int _totalCount;
    private bool _loading;
    private int? _loadedServerId;
    private int _loadGeneration;
    private Aetheus.Front.Components.Shared.AetheusDataGrid<ProjectDto>? _grid;
    private Func<string, string>? _statusText;

    // Recette R-210: what the Status header filter shows for each state.
    private Func<string, string> StatusText => _statusText ??= GridFilterText.ForEnum<ProjectStatus>(L);

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedServerId == ServerId) return;
        _loadedServerId = ServerId;
        Interlocked.Increment(ref _loadGeneration);
        _projects = [];
        _totalCount = 0;
        if (_grid is not null) await _grid.Reload();
    }

    /// <summary>Recette R2-124: one page of the export, with the grid's sort and column filters.</summary>
    private Task<PaginatedResult<ProjectDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = GetSort(args);
        return Api.Servers.GetServerProjectsPageAsync(ServerId, page, pageSize, sortBy: sortBy, sortDescending: sortDescending, filters: args.ToApiFilters());
    }

    private async Task LoadProjectsAsync(GridLoadArgs args)
    {
        var serverId = ServerId;
        var generation = _loadGeneration;
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = GetSort(args);
        // Recette R-210 / R-224: the header filters, applied by the API.
        var filters = args.ToApiFilters();
        _loading = true;
        try
        {
            var result = await Api.Servers.GetServerProjectsPageAsync(serverId, page, pageSize, sortBy: sortBy, sortDescending: sortDescending, filters: filters);
            if (ServerId == serverId && generation == _loadGeneration)
            {
                _projects = result.Items;
                _totalCount = result.TotalCount;
            }
        }
        catch (HttpRequestException)
        {
            if (ServerId == serverId && generation == _loadGeneration)
            {
                _projects = [];
                _totalCount = 0;
            }
        } // 401 on expired JWT - redirect handled by AuthProvider
        finally
        {
            if (ServerId == serverId && generation == _loadGeneration)
                _loading = false;
        }
    }

    private static (string SortBy, bool SortDescending) GetSort(GridLoadArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy)) return ("Name", false);
        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase));
    }

    private void OnRowClick(ProjectDto project)
    {
        Nav.NavigateTo($"/projects/{project.Id}/overview");
    }
}
