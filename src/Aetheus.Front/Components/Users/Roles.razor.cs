// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Users;

public partial class Roles : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private AdminIdentitySearch Search { get; set; } = default!;

    private List<RoleDto> _roles = [];
    private AetheusDataGrid<RoleDto>? _grid;
    private int _totalCount;
    private int _page = 1;
    private int _pageSize = 20;
    private string? _sortBy;
    private bool _sortDescending;
    private List<Aetheus.Shared.Components.Shared.GridFilter> _columnFilters = [];
    // Must start false so the grid emits the initial LoadData callback. Starting with IsLoading=true
    // suppresses that callback and leaves the grid in a permanent loading state.
    private bool _loading;
    private int _silentRefreshDepth;
    // RT4M: shared admin-hub subscription wrapper, owned and disposed by this page.
    private AdminEntitySubscription? _adminRt;

    protected override async Task OnInitializedAsync()
    {
        if (!AdminPageEntry.TryEnter(Auth, Nav, Breadcrumb, L, "Roles"))
            return;

        // The search shared with Users and Organizations, in the section bar.
        Search.Changed += ResetSearchAsync;

        // Realtime: refresh the roles list on any create/update/delete/clone/permission change. RT4M.
        _adminRt = new AdminEntitySubscription(HubFactory);
        await _adminRt.StartAsync(AdminEntities.Role, () => InvokeAsync(async () =>
        {
            // A pushed change refreshes the grid quietly (rows, page, filters kept).
            try { await RefreshLiveAsync(); } catch (HttpRequestException) { } // hub-triggered refresh - silent on auth failure
            StateHasChanged();
        }));

        // The grid does not consistently raise its first LoadData callback when this page is reached
        // through client-side navigation. Load explicitly so the grid cannot stay at a false "0 total"
        // until the user presses Refresh.
        await LoadPageAsync();
    }

    private async Task LoadAsync()
    {
        if (_grid is not null)
        {
            await _grid.Reload();
            return;
        }

        await LoadPageAsync();
    }

    private async Task LoadDataAsync(GridLoadArgs args)
    {
        _pageSize = args.Top ?? 20;
        _page = ((args.Skip ?? 0) / _pageSize) + 1;
        (_sortBy, _sortDescending) = ResolveSort(args);
        // The header filters, applied by the API.
        _columnFilters = args.ToApiFilters();
        await LoadPageAsync();
    }

    private Task ResetSearchAsync()
    {
        _page = 1;
        return _grid is not null ? _grid.GoToPage(0, forceReload: true) : LoadPageAsync();
    }

    /// <summary>One page of the export, with the search, sort and column filters of the
    /// grid.</summary>
    private Task<PaginatedResult<RoleDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(20);
        var (sortBy, sortDescending) = ResolveSort(args);
        return Api.Auth.GetRoleDtosAsync(page, pageSize, Search.Search, sortBy: sortBy, sortDescending: sortDescending,
            filters: args.ToApiFilters());
    }

    private async Task LoadPageAsync()
    {
        var showLoading = _silentRefreshDepth == 0;
        if (showLoading) _loading = true;
        try
        {
            var result = await Api.Auth.GetRoleDtosAsync(
                _page, _pageSize, Search.Search, sortBy: _sortBy, sortDescending: _sortDescending, filters: _columnFilters);
            _roles = result.Items;
            _totalCount = result.TotalCount;
        }
        finally
        {
            if (showLoading) _loading = false;
        }
    }

    private async Task RefreshLiveAsync()
    {
        _silentRefreshDepth++;
        try
        {
            if (_grid is not null)
                await _grid.Refresh();
            else
                await LoadPageAsync();
        }
        finally
        {
            _silentRefreshDepth--;
        }
    }

    private async Task RefreshSilentlyAsync()
    {
        _silentRefreshDepth++;
        try
        {
            await LoadAsync();
        }
        finally
        {
            _silentRefreshDepth--;
        }
    }

    private static (string? SortBy, bool Descending) ResolveSort(GridLoadArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy)) return ("Name", false);
        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase));
    }

    private void OnRowClick(RoleDto role) => Nav.NavigateTo($"/admin/roles/{role.Id}");

    private async Task OnCreate()
    {
        var created = await Dialog.OpenAsync<RoleCreateDialog>(L["NewRole"],
            new Dictionary<string, object?>(),
            new OmniDialogOptions { Width = "520px", CloseDialogOnOverlayClick = false, AutoFocusFirstElement = false });

        // Land straight on the new role's page so the admin can set its permission matrix.
        if (created is RoleDto role)
            Nav.NavigateTo($"/admin/roles/{role.Id}");
    }

    private async Task OnDelete(RoleDto role)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["ConfirmDeleteRole"], role.Name),
            L["Delete"],
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"], CancelButtonText = L["GoBack"] });

        if (confirmed != true) return;

        var success = await Api.Auth.DeleteRoleAsync(role.Id);
        if (success)
        {
            await RefreshSilentlyAsync();
            Toast.Success("Deleted", "Deleted");
        }
        else
        {
            Toast.Error("Error", "DeleteFailed");
        }
    }

    private async Task OnClone(RoleDto role)
    {
        var cloned = await Api.Auth.CloneRoleAsync(role.Id);
        if (cloned is not null)
        {
            Toast.Success("Created", "Saved");
            Nav.NavigateTo($"/admin/roles/{cloned.Id}");
        }
        else
        {
            Toast.Error("Error", "SaveFailed");
        }
    }

    /// <summary>
    /// Localizes well-known seeded role descriptions (F-24). Falls back to the raw value
    /// for user-created roles.
    /// </summary>
    private string LocalizeDescription(RoleDto role) => role.Name switch
    {
        "Admin" => L["RoleAdminDescription"],
        "Reader" => L["RoleReaderDescription"],
        "Contributor" => L["RoleContributorDescription"],
        _ => role.Description ?? string.Empty
    };

    public async ValueTask DisposeAsync()
    {
        Search.Changed -= ResetSearchAsync;
        if (_adminRt is not null) await _adminRt.DisposeAsync();
    }
}
