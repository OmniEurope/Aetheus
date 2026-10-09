// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.ServiceConnections;

public partial class ServiceConnections : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private UiActions Ui { get; set; } = default!;
    [Inject] private NotifyHelper Notify { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;

    private List<ServiceConnectionDto> _items = [];
    private int _count;
    private bool _loading;
    private string? _search;
    private bool _canWrite;
    private readonly HashSet<int> _testing = [];
    private readonly Dictionary<int, ServiceConnectionTestResultDto> _testResults = [];
    private HubConnection? _hubConnection;
    private AetheusDataGrid<ServiceConnectionDto>? _grid;

    // Recette R-224: the grid's header filters, sent with every page request, and the values they offer.
    private List<GridFilter> _columnFilters = [];
    private ServiceConnectionFilterValuesDto _filterValues = new();
    private Func<string, string>? _typeText;
    private Func<string, string> TypeText => _typeText ??= GridFilterText.ForEnum<ServiceConnectionType>(L);

    protected override async Task OnInitializedAsync()
    {
        Breadcrumb.Set(new BreadcrumbItem(L["ServiceConnections"]));
        // Permissions may land after the page mounts (MainLayout loads them in parallel);
        // subscribe so the create/edit/delete affordances reactivate the moment they arrive.
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshCanWrite();
        try { await LoadData(new GridLoadArgs()); }
        catch (HttpRequestException) { } // 401 on expired JWT - redirect handled by AuthProvider
        await LoadFilterValuesAsync();
        await StartRealtimeAsync();
    }

    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        InvokeAsync(StateHasChanged);
    }

    private void RefreshCanWrite() => _canWrite = Permissions.CanWrite(ResourceType.ServiceConnection);

    private async Task LoadFilterValuesAsync()
    {
        try { _filterValues = await Api.Settings.GetServiceConnectionFilterValuesAsync(); }
        catch (HttpRequestException) { _filterValues = new ServiceConnectionFilterValuesDto(); }
    }

    /// <summary>Recette R2-124: one page of the export, with the search, sort and column filters of the grid.</summary>
    internal Task<PaginatedResult<ServiceConnectionDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(20);
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(ServiceConnectionDto.Name));
        return Api.Settings.GetServiceConnectionsAsync(page, pageSize, _search,
            sortBy: sortBy, sortDescending: sortDescending, filters: args.ToApiFilters());
    }

    internal async Task LoadData(GridLoadArgs args)
    {
        _loading = true;
        var (page, pageSize) = args.ToPageRequest(20);
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(ServiceConnectionDto.Name));
        _columnFilters = args.ToApiFilters();
        var result = await Api.Settings.GetServiceConnectionsAsync(page, pageSize, _search,
            sortBy: sortBy, sortDescending: sortDescending, filters: _columnFilters);
        _items = result.Items;
        _count = result.TotalCount;
        _loading = false;
        StateHasChanged();
    }

    // Recette R-226: a live change refreshes the grid in place. It used to call LoadData(new GridLoadArgs()),
    // which fetched page 1 with no sort and no filter while the headers still showed them.
    private async Task RefreshGridAsync()
    {
        try
        {
            if (_grid is not null) await _grid.Refresh();
            else await LoadData(new GridLoadArgs());
        }
        catch (HttpRequestException) { } // background refresh: a failed reload keeps the current grid
    }

    // A user action (search, create, edit, delete) reloads through the grid, which keeps its sort and filters.
    private async Task ReloadGridAsync()
    {
        try
        {
            if (_grid is not null) await _grid.Reload();
            else await LoadData(new GridLoadArgs());
        }
        catch (HttpRequestException) { } // post-action reload: a failed reload keeps the current grid
    }

    private async Task StartRealtimeAsync()
    {
        try
        {
            _hubConnection = HubFactory.Create("entities");
            _hubConnection.On<ResourceType, int, string>("EntityChanged", (type, _, _) =>
                type == ResourceType.ServiceConnection
                    ? InvokeAsync(RefreshGridAsync)
                    : Task.CompletedTask);
            _hubConnection.RejoinOnReconnect(() => InvokeAsync(async () =>
            {
                await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.ServiceConnection);
                await RefreshGridAsync();
            }));
            await _hubConnection.StartAsync();
            await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.ServiceConnection);
        }
        catch { /* SignalR is best-effort; local mutations still reload explicitly. */ }
    }

    private async Task TestAsync(int id)
    {
        _testing.Add(id);
        _testResults.Remove(id);
        StateHasChanged();
        try
        {
            var result = await Api.Settings.TestServiceConnectionAsync(id);
            // A missing connection (deleted meanwhile) is an honest "error", never a green.
            _testResults[id] = result ?? new ServiceConnectionTestResultDto
            {
                Status = ServiceConnectionTestStatus.Error,
                Message = L["ServiceConnectionNotFound"]
            };
            var testResult = _testResults[id];
            Notify.Notify(
                testResult.Status == ServiceConnectionTestStatus.Valid
                    ? OmniSeverity.Success
                    : OmniSeverity.Danger,
                "TestConnection",
                testResult.Message ?? L["ServiceConnectionTestFailed"]);
        }
        catch (HttpRequestException)
        {
            _testResults[id] = new ServiceConnectionTestResultDto
            {
                Status = ServiceConnectionTestStatus.Error,
                Message = L["ServiceConnectionTestFailed"]
            };
            Notify.Error("Error", "ServiceConnectionTestFailed");
        }
        finally
        {
            _testing.Remove(id);
            StateHasChanged();
        }
    }

    private Task OpenCreateDialogAsync() => OpenEditDialogAsync(null);

    private async Task OpenEditDialogAsync(ServiceConnectionDto? connection)
    {
        var result = await Dialog.OpenAsync<ServiceConnectionEditDialog>(
            connection is null ? L["Create"] : L["Edit"],
            new Dictionary<string, object?> { ["ConnectionId"] = connection?.Id },
            new OmniDialogOptions { Width = "640px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });

        if (result is true)
        {
            _testResults.Clear(); // creds may have changed; drop stale badges
            await LoadFilterValuesAsync();
            await ReloadGridAsync();
        }
    }

    private async Task DeleteAsync(ServiceConnectionDto connection)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["DeleteServiceConnectionConfirm"], connection.Name), L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await Ui.RunAsync(
            () => Api.Settings.DeleteServiceConnectionAsync(connection.Id),
            "Deleted",
            async () => { _testResults.Remove(connection.Id); await ReloadGridAsync(); },
            errorKey: "DeleteFailed",
            successTitleKey: "Deleted");
    }

    private static OmniTone StatusBadge(ServiceConnectionTestStatus status) => status switch
    {
        ServiceConnectionTestStatus.Valid => OmniTone.Success,
        ServiceConnectionTestStatus.Invalid => OmniTone.Danger,
        ServiceConnectionTestStatus.Unsupported => OmniTone.Neutral,
        _ => OmniTone.Warning
    };

    public async ValueTask DisposeAsync()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        if (_hubConnection is null) return;
        try { await _hubConnection.InvokeAsync("LeaveEntityUpdates", ResourceType.ServiceConnection); }
        catch { /* best-effort */ }
        await _hubConnection.DisposeAsync();
        _hubConnection = null;
    }
}
