// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerAppsSection
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    private List<ServerAppDto>? _apps;
    private int? _loadedServerId;
    private AetheusDataGrid<ServerAppDto>? _grid;
    private int _totalCount;
    private int _page = 1;
    private int _pageSize = 25;
    private string _sortBy = "Name";
    private bool _sortDescending;
    private string? _search;
    private bool _loading;
    private bool _addVisible;
    private bool _addSaving;
    private string _addName = string.Empty;
    private string _addVersion = string.Empty;
    private int? _addPort;
    private string _addSource = "manual";
    private static readonly string[] _sources = ["manual", "systemd", "docker"];
    private List<Aetheus.Shared.Components.Shared.GridFilter> _columnFilters = [];
    private List<string> _sourceValues = [];
    private Func<string, string>? _statusText;

    // The Status filter shows each state as the column does.
    private Func<string, string> StatusText => _statusText ??= GridFilterText.ForEnum<ServerAppStatus>(L);

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedServerId == ServerId) return;
        _loadedServerId = ServerId;
        await Task.WhenAll(ReloadAppsAsync(), LoadSourceValuesAsync());
    }

    // The sources the Source header filter offers, across every application of the server.
    private async Task LoadSourceValuesAsync()
    {
        var serverId = ServerId;
        try
        {
            var values = await Api.Servers.GetServerAppFilterValuesAsync(serverId);
            if (ServerId == serverId) _sourceValues = values.Sources;
        }
        catch (HttpRequestException)
        {
            if (ServerId == serverId) _sourceValues = [];
        }
    }

    /// <summary>One page of the export, with the search, sort and column filters of the
    /// grid.</summary>
    private Task<PaginatedResult<ServerAppDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest("Name");
        return Api.Servers.GetServerAppsPageAsync(
            ServerId, page, pageSize, _search, sortBy, sortDescending, filters: args.ToApiFilters());
    }

    private async Task LoadAsync()
    {
        var serverId = ServerId;
        _loading = true;
        try
        {
            var result = await Api.Servers.GetServerAppsPageAsync(
                serverId, _page, _pageSize, _search, _sortBy, _sortDescending, filters: _columnFilters);
            if (ServerId != serverId) return;
            _apps = result.Items;
            _totalCount = result.TotalCount;
        }
        catch (HttpRequestException)
        {
            if (ServerId != serverId) return;
            _apps = [];
            _totalCount = 0;
        }
        finally { if (ServerId == serverId) _loading = false; }
    }

    private async Task OnLoadDataAsync(GridLoadArgs args)
    {
        (_page, _pageSize) = args.ToPageRequest();
        (_sortBy, _sortDescending) = args.ToSortRequest("Name");
        // The header filters, applied by the API.
        _columnFilters = args.ToApiFilters();
        await LoadAsync();
    }

    private Task OnSearchChangedAsync() => ReloadAppsAsync();

    // The grid scrolls (remote virtualization) and only shows what it fetched itself, so another
    // server, a search or a new application reloads it from its first row, a deletion in place.
    private Task ReloadAppsAsync()
    {
        _page = 1;
        return _grid is not null ? _grid.GoToPage(0, forceReload: true) : LoadAsync();
    }

    private Task RefreshAppsAsync() => _grid is not null ? _grid.Refresh() : LoadAsync();

    private async Task AddAppAsync()
    {
        _addSaving = true;
        var result = await Api.Servers.CreateServerAppAsync(ServerId, new CreateServerAppRequest
        {
            Name = _addName,
            Version = string.IsNullOrWhiteSpace(_addVersion) ? null : _addVersion,
            Port = _addPort,
            Source = _addSource
        });
        if (result is not null)
        {
            await Task.WhenAll(ReloadAppsAsync(), LoadSourceValuesAsync());
            _addVisible = false;
            _addName = string.Empty;
            _addVersion = string.Empty;
            _addPort = null;
            Toast.Success("Created", "ApplicationCreated");
        }
        _addSaving = false;
    }

    private async Task DeleteAppAsync(ServerAppDto app)
    {
        var confirmed = await Dialog.Confirm(L["DeleteConfirm"].Value, L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        var success = await Api.Servers.DeleteServerAppAsync(ServerId, app.Id);
        if (success)
        {
            await RefreshAppsAsync();
            Toast.Success("Deleted", "Deleted");
        }
        else
        {
            Toast.Error("Error", "DeleteFailed");
        }
    }

    private static OmniTone GetAppStatusBadge(ServerAppStatus status) => status switch
    {
        ServerAppStatus.Running => OmniTone.Success,
        ServerAppStatus.Stopped => OmniTone.Neutral,
        ServerAppStatus.Error => OmniTone.Danger,
        _ => OmniTone.Warning
    };

}
