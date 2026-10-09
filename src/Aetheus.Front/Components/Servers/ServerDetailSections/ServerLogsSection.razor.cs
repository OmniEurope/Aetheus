// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerLogsSection
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    private AetheusDataGrid<TaskLogDto>? _grid;
    private List<TaskLogDto> _logs = [];
    private int _totalCount;
    private int? _loadedServerId;
    private bool _loading;
    private bool _loadFailed;

    // Recette R-210: the level filter lists the translated levels, built once for a stable delegate.
    private Func<string, string>? _levelText;
    private Func<string, string> LevelText => _levelText ??= GridFilterText.ForEnum<TaskLogLevel>(L);

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedServerId == ServerId) return;
        var switched = _loadedServerId is not null;
        _loadedServerId = ServerId;
        // The first load comes from the grid itself; another server on the same instance starts over
        // from its first block, with the filters the reader set.
        if (switched && _grid is not null)
        {
            _logs = [];
            _totalCount = 0;
            await _grid.Reload();
        }
    }

    /// <summary>
    /// Recette R-210 / R-224: every block the grid asks for goes to the API with the header filters and
    /// the sort, so the count and the rows describe the whole log of the server.
    /// </summary>
    /// <summary>Recette R2-124: one page of the export, with the grid's sort and column filters.</summary>
    private Task<PaginatedResult<TaskLogDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(defaultPageSize: 50);
        var sort = args.Sorts?.FirstOrDefault();
        return Api.Servers.GetServerLogsAsync(ServerId, page, pageSize, args.ToApiFilters(),
            sortBy: sort?.Property, sortDescending: sort?.SortOrder == GridSortOrder.Descending);
    }

    private async Task LoadLogsAsync(GridLoadArgs args)
    {
        var serverId = ServerId;
        var (page, pageSize) = args.ToPageRequest(defaultPageSize: 50);
        var filters = args.ToApiFilters();
        var sort = args.Sorts?.FirstOrDefault();
        _loading = true;
        _loadFailed = false;
        try
        {
            var result = await Api.Servers.GetServerLogsAsync(
                serverId, page, pageSize, filters,
                sortBy: sort?.Property,
                sortDescending: sort?.SortOrder == GridSortOrder.Descending);
            if (ServerId != serverId) return;
            _logs = result.Items;
            _totalCount = result.TotalCount;
        }
        catch (HttpRequestException)
        {
            if (ServerId != serverId) return;
            _logs = [];
            _totalCount = 0;
            _loadFailed = true;
        } // 401 on expired JWT - redirect handled by AuthProvider
        finally
        {
            if (ServerId == serverId) _loading = false;
        }
    }

    private static OmniTone GetLevelBadge(TaskLogLevel level) => DisplayFormatting.TaskLogBadge(level);
}
