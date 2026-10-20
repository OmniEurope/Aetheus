// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerLogsSection
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    private AetheusDataGrid<TaskLogDto>? _grid;
    private List<TaskLogDto> _logs = [];
    private int _totalCount;
    private int? _loadedServerId;
    private bool _loading;
    private bool _loadFailed;

    // The level filter lists the translated levels, built once for a stable delegate.
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

    /// <summary>Reads an export page with the same sorting and column filters as the
    /// grid.</summary>
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

    // Recette R3-35: clicking a line opens that exact entry.
    private Task OpenEntryAsync(TaskLogDto log) => Logs.LogEntryDialog.OpenAsync(Dialog, L, log.Message);

    private static OmniTone GetLevelBadge(TaskLogLevel level) => DisplayFormatting.TaskLogBadge(level);
}
