// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Users;
using Aetheus.Shared.Components.Organizations;
namespace Aetheus.Front.Components.Organizations;

public partial class Organizations : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private NotifyHelper Notify { get; set; } = default!;
    [Inject] private AdminIdentitySearch Search { get; set; } = default!;

    private List<OrganizationDto> _items = [];
    private Aetheus.Front.Components.Shared.AetheusDataGrid<OrganizationDto>? _grid;
    private int _totalCount;
    private bool _loading;
    private int _silentRefreshDepth;
    // RT4M: shared admin-hub subscription wrapper, owned and disposed by this page.
    private AdminEntitySubscription? _adminRt;

    protected override async Task OnInitializedAsync()
    {
        if (!Aetheus.Front.Components.Shared.AdminPageEntry.TryEnter(Auth, Nav, Breadcrumb, L, "Organizations"))
            return;

        // Recette R-316: the search shared with Users and Roles, in the section bar.
        Search.Changed += ResetSearchAsync;

        // Realtime: refresh the list on any org create/update/delete, and on member/project changes
        // (which move the Members/Projects counts shown in the grid). RT4M.
        _adminRt = new AdminEntitySubscription(HubFactory);
        await _adminRt.StartAsync(AdminEntities.Organization, () => InvokeAsync(async () =>
        {
            // Recette R-226: a pushed change refreshes the grid quietly (rows, page, filters kept).
            await RefreshSilentlyAsync(() => _grid?.Refresh() ?? Task.CompletedTask);
            StateHasChanged();
        }));
    }

    /// <summary>Recette R2-124: one page of the export, with the search, sort and column filters of the grid.</summary>
    private async Task<PaginatedResult<OrganizationDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest("Name");
        return await Api.Servers.GetOrganizationsAsync(Search.Search, page, pageSize, sortBy, sortDescending, filters: args.ToApiFilters())
            ?? new PaginatedResult<OrganizationDto>();
    }

    private async Task LoadDataAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest("Name");
        // Recette R-210: the header filters, applied by the API.
        var filters = args.ToApiFilters();
        var showLoading = _silentRefreshDepth == 0;
        if (showLoading) _loading = true;
        try
        {
            var result = await Api.Servers.GetOrganizationsAsync(Search.Search, page, pageSize, sortBy, sortDescending, filters: filters);
            _items = result?.Items ?? [];
            _totalCount = result?.TotalCount ?? 0;
        }
        finally
        {
            if (showLoading) _loading = false;
        }
    }

    private Task ReloadAsync() => _grid?.Reload() ?? Task.CompletedTask;

    private async Task RefreshSilentlyAsync(Func<Task>? refresh = null)
    {
        _silentRefreshDepth++;
        try
        {
            await (refresh ?? ReloadAsync)();
        }
        finally
        {
            _silentRefreshDepth--;
        }
    }

    // Recette R-316: forced, the first page reloads even when the grid is already on it.
    private Task ResetSearchAsync() => _grid?.GoToPage(0, forceReload: true) ?? Task.CompletedTask;

    private async Task OnCreate()
    {
        // Creation stays a lightweight dialog; on success go straight to the new org's detail page.
        var result = await Dialog.OpenAsync<OrganizationDialog>(L["NewOrganization"],
            new Dictionary<string, object?>(),
            new OmniDialogOptions { Width = "560px", CloseDialogOnOverlayClick = false, AutoFocusFirstElement = false });
        if (result is OrganizationDto created)
            Nav.NavigateTo($"/admin/organizations/{created.Id}");
    }

    private void OpenDetail(OrganizationDto org)
        => Nav.NavigateTo($"/admin/organizations/{org.Id}");

    private async Task OnDelete(OrganizationDto org)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["ConfirmDeleteOrganization"], org.Name),
            L["Delete"],
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"], CancelButtonText = L["GoBack"] });
        if (confirmed != true) return;
        if (await Api.Servers.DeleteOrganizationAsync(org.Id))
        {
            await RefreshSilentlyAsync();
            Notify.Success("Deleted", "Deleted");
        }
        else
        {
            Notify.Error("Error", "DeleteFailed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Search.Changed -= ResetSearchAsync;
        if (_adminRt is not null) await _adminRt.DisposeAsync();
    }
}
