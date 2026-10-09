// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Plugins;

public partial class PluginManagement : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;

    private List<PluginRegistrationDto> _plugins = [];
    private AetheusDataGrid<PluginRegistrationDto>? _grid;
    private int _totalCount;
    private int _currentPage = 1;
    private int _pageSize = 25;
    private string _sortBy = "Name";
    private bool _sortDescending;
    private string? _search;
    private bool _loading = true;
    private bool _authorized;
    // RT4M: shared admin-hub subscription wrapper, owned and disposed by this page.
    private AdminEntitySubscription? _adminRt;

    // Recette R-224: the grid's header filters, sent with every page request, and the values they offer.
    private List<GridFilter> _columnFilters = [];
    private PluginFilterValuesDto _filterValues = new();
    private Func<string, string>? _typeText;
    private Func<string, string>? _statusText;
    private Func<string, string> TypeText => _typeText ??= GridFilterText.ForEnum<PluginType>(L);
    private Func<string, string> StatusText => _statusText ??= GridFilterText.ForEnum<PluginStatus>(L);

    protected override async Task OnInitializedAsync()
    {
        if (!Auth.IsAdmin)
        {
            Navigation.NavigateTo("/");
            return;
        }

        _authorized = true;
        Breadcrumb.Set(
            new BreadcrumbItem(L["Administration"], "/admin"),
            new BreadcrumbItem(L["Plugins"]));
        await LoadPageAsync();
        await LoadFilterValuesAsync();
        // Realtime: refresh the list when plugins are registered/updated/unregistered (e.g. an agent
        // registering a plugin), so the management view reflects changes without a manual reload. RT4M.
        _adminRt = new AdminEntitySubscription(HubFactory);
        // Recette R-226: the grid refreshes in place (page, sort and filters kept, new rows highlighted).
        await _adminRt.StartAsync(AdminEntities.Plugin, () => InvokeAsync(async () =>
        {
            await LoadFilterValuesAsync();
            if (_grid is not null) await _grid.Refresh();
            else await LoadPageAsync();
            StateHasChanged();
        }));
    }

    private async Task LoadPageAsync()
    {
        _loading = true;
        try
        {
            var result = await Api.Settings.GetPluginsPageAsync(
                _currentPage, _pageSize, _search, _sortBy, _sortDescending, filters: _columnFilters);
            _plugins = result.Items;
            _totalCount = result.TotalCount;
        }
        catch (HttpRequestException)
        {
            _plugins = [];
            _totalCount = 0;
        }
        finally { _loading = false; }
    }

    private async Task LoadFilterValuesAsync()
    {
        try { _filterValues = await Api.Settings.GetPluginFilterValuesAsync(); }
        catch (HttpRequestException) { _filterValues = new PluginFilterValuesDto(); }
    }

    /// <summary>Recette R2-124: one page of the export, with the search, sort and column filters of the grid.</summary>
    internal Task<PaginatedResult<PluginRegistrationDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest("Name");
        return Api.Settings.GetPluginsPageAsync(page, pageSize, _search, sortBy, sortDescending, filters: args.ToApiFilters());
    }

    internal async Task OnLoadDataAsync(GridLoadArgs args)
    {
        (_currentPage, _pageSize) = args.ToPageRequest();
        (_sortBy, _sortDescending) = args.ToSortRequest("Name");
        _columnFilters = args.ToApiFilters();
        await LoadPageAsync();
    }

    private Task OnSearchChangedAsync()
    {
        _currentPage = 1;
        return ReloadGridAsync();
    }

    // Recette R-327: the grid scrolls (remote virtualization) and only shows what it fetched itself, so a
    // change goes through it: a new search or a registration from the first row, a toggle in place.
    private Task ReloadGridAsync() => _grid is not null ? _grid.GoToPage(0, forceReload: true) : LoadPageAsync();
    private Task RefreshGridAsync() => _grid is not null ? _grid.Refresh() : LoadPageAsync();

    // X4D8: the register form now lives in PluginRegisterDialog. Reload the list when it reports success.
    private async Task OpenRegisterDialog()
    {
        var result = await Dialog.OpenAsync<PluginRegisterDialog>(
            L["Register"], new Dictionary<string, object?>(), PluginRegisterDialog.OmniDialogOptions());
        if (result is true) await ReloadGridAsync();
    }

    private async Task TogglePlugin(PluginRegistrationDto plugin)
    {
        var newStatus = plugin.Status == PluginStatus.Enabled ? PluginStatus.Disabled : PluginStatus.Enabled;
        var result = await Api.Settings.UpdatePluginAsync(plugin.Id, new UpdatePluginRequest
        {
            Description = plugin.Description,
            Status = newStatus,
            EntryPoint = plugin.EntryPoint,
            ConfigurationJson = plugin.ConfigurationJson
        });
        if (result is not null)
        {
            await RefreshGridAsync();
            Toast.Success("Saved", "Saved");
        }
        else
        {
            Toast.Error("Error", "SaveFailed");
        }
    }

    private async Task UnregisterPlugin(int id)
    {
        var confirmed = await Dialog.Confirm(L["DeleteConfirm"].Value, L["Unregister"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Unregister"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        var success = await Api.Settings.UnregisterPluginAsync(id);
        if (success)
        {
            await ReloadGridAsync();
            Toast.Success("Deleted", "PluginUnregistered");
        }
        else
        {
            Toast.Error("Error", "DeleteFailed");
        }
    }

    private static OmniTone GetStatusBadgeStyle(PluginStatus status) => status switch
    {
        PluginStatus.Enabled => OmniTone.Success,
        PluginStatus.Disabled => OmniTone.Warning,
        PluginStatus.Error => OmniTone.Danger,
        _ => OmniTone.Accent
    };

    public async ValueTask DisposeAsync()
    {
        if (_adminRt is not null) await _adminRt.DisposeAsync();
    }
}
