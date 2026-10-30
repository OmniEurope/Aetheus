// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Users;

public partial class Users : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private AdministrationScopeService Scope { get; set; } = default!;

    private AetheusDataGrid<UserDto>? _grid;
    private List<UserDto> _users = [];
    private int _totalCount;
    private bool _loading;
    private bool _loadFailed;
    private int _silentRefreshDepth;
    private List<Aetheus.Shared.Components.Shared.GridFilter> _columnFilters = [];
    private List<string> _roleNames = [];
    private Func<string, string>? _yesNoText;
    private Func<string, string> YesNoText => _yesNoText ??= GridFilterText.YesNo(L);
    // RT4M: the shared admin-hub subscription wrapper, owned and disposed by this page.
    private AdminEntitySubscription? _adminRt;

    protected override async Task OnInitializedAsync()
    {
        if (!AdminPageEntry.TryEnter(Auth, Permissions, PermissionKeys.OrganizationMembersManage, Nav, Breadcrumb, L, "Users"))
            return;

        // The organization switcher's choice filters the list; it is known before the grid asks its first page.
        await Scope.EnsureLoadedAsync();
        // An organization's administrator reads its organizations' users, nothing more (owner decision
        // 2026-10-28): no live feed of every account, no list of the site-wide roles.
        if (!Auth.IsAdmin)
            return;

        // Realtime: reload the grid whenever any session creates/updates/deletes a user (the creator
        // also receives this broadcast, so the list refreshes even within the acting session). RT4M.
        _adminRt = new AdminEntitySubscription(HubFactory);
        await _adminRt.StartAsync(AdminEntities.User,
            () => InvokeAsync(async () =>
            {
                Cache.InvalidatePrefix("users:");
                // A change pushed by the hub refreshes the grid quietly (rows, page, filters kept).
                await RefreshSilentlyAsync(() => _grid?.Refresh() ?? Task.CompletedTask);
            }));
        // Stale-while-revalidate: pre-seed the grid from the cached default view (grid PageSize = 20)
        // so revisiting /admin/users paints instantly; OnLoadData then revalidates in the background.
        Cache.Seed<PaginatedResult<UserDto>>(CacheKey(1, 20), ApplyUsers);
        // The shared grid requests its first page after its columns are registered.
        await LoadRoleNamesAsync();
    }

    // The role names the Roles header filter offers (every role, not only those on screen).
    private async Task LoadRoleNamesAsync()
    {
        try
        {
            _roleNames = await Api.Auth.GetRolesAsync();
        }
        catch (HttpRequestException)
        {
            _roleNames = [];
        }
    }

    // The sort is part of the key: without it two different orders share one cache entry and the
    // second one is served the first one's rows, which looks exactly like a sort that does nothing.
    private string CacheKey(int page, int pageSize, string? sortBy = null, bool sortDescending = false) =>
        $"users:{Scope.SelectedFor(PermissionKeys.OrganizationMembersManage)}:{page}:{pageSize}:{sortBy}:{sortDescending}:{GridColumnFilters.CacheText(_columnFilters)}";

    private void ApplyUsers(PaginatedResult<UserDto> result)
    {
        _users = result.Items;
        _totalCount = result.TotalCount;
    }

    /// <summary>One page of the export, with the sort and column filters of the
    /// grid.</summary>
    private Task<PaginatedResult<UserDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(UserDto.Username));
        return Api.Auth.GetUsersAsync(page, pageSize, null, sortBy, sortDescending, args.ToApiFilters(),
            organizationId: Scope.SelectedFor(PermissionKeys.OrganizationMembersManage));
    }

    private async Task OnLoadData(GridLoadArgs args)
    {
        _loadFailed = false;
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(UserDto.Username));
        // The header filters, applied by the API (and part of the cache key).
        _columnFilters = args.ToApiFilters();
        var filters = _columnFilters;
        var showLoading = _silentRefreshDepth == 0;
        var organizationId = Scope.SelectedFor(PermissionKeys.OrganizationMembersManage);
        await Cache.RevalidateAsync(
            CacheKey(page, pageSize, sortBy, sortDescending),
            () => Api.Auth.GetUsersAsync(page, pageSize, null, sortBy, sortDescending, filters, organizationId: organizationId),
            ApplyUsers,
            loading =>
            {
                if (showLoading || !loading) _loading = loading;
            },
            () => InvokeAsync(StateHasChanged),
            _ => _loadFailed = true);
    }

    private async Task RetryLoadAsync()
    {
        if (_grid is not null) await _grid.Reload();
    }

    private async Task RefreshSilentlyAsync(Func<Task> refresh)
    {
        _silentRefreshDepth++;
        try
        {
            await refresh();
        }
        finally
        {
            _silentRefreshDepth--;
        }
    }

    private void OnCreate() => Nav.NavigateTo("/admin/users/new");

    /// <summary>The account page is the site Admin's; an organization's administrator manages members from its
    /// organization's page.</summary>
    private void OnRowClick(UserDto user)
    {
        if (Auth.IsAdmin) Nav.NavigateTo($"/users/{user.Id}");
    }

    private Task OnOrganizationChangedAsync() => _grid?.Reload() ?? Task.CompletedTask;

    private bool IsSelf(UserDto user) => Auth.UserId == user.Id;

    // R-25: the row switch saves the account with its other fields unchanged, no confirmation. Switching off
    // an owner of pipelines opens the handover dialog of the edit page (who takes the pipelines over) and the
    // same request carries its answer; dismissing it leaves the account active. The server refuses the change
    // on one's own account.
    private async Task OnActiveChangedAsync(UserDto user, bool isActive)
    {
        if (IsSelf(user) && !isActive)
            return;
        UserPipelineHandover? handover = null;
        if (!isActive)
        {
            List<PipelineOwnershipDto> owned;
            try
            {
                owned = await Api.Pipelines.GetOwnedPipelinesAsync(user.Username);
            }
            catch (HttpRequestException)
            {
                Toast.Error(L["Error"], L["SaveFailed"]);
                await RefreshSilentlyAsync(() => _grid?.Reload() ?? Task.CompletedTask);
                return;
            }
            if (owned.Count > 0)
            {
                handover = await AskPipelineHandoverAsync(user.Username);
                if (handover is null)
                {
                    await RefreshSilentlyAsync(() => _grid?.Reload() ?? Task.CompletedTask);
                    return;
                }
            }
        }
        var request = new UpdateUserRequest
        {
            Username = user.Username,
            Email = user.Email,
            IsActive = isActive,
            MustChangePassword = user.MustChangePassword,
            Roles = user.Roles,
            TransferPipelinesToUsername = handover?.TransferTo,
            ForceWithOwnedPipelines = handover?.Force ?? false
        };
        var updated = await Api.Auth.UpdateUserAsync(user.Id, request);
        if (updated is not null)
            Toast.Success(L["Saved"], isActive ? L["UserActivated"] : L["UserDeactivated"]);
        else
            Toast.Error(L["Error"], L["SaveFailed"]);
        await RefreshSilentlyAsync(() => _grid?.Reload() ?? Task.CompletedTask);
    }

    private async Task<UserPipelineHandover?> AskPipelineHandoverAsync(string username) =>
        await Dialog.OpenAsync<UserPipelineHandoverDialog>(L["DeactivateUser"].Value, new Dictionary<string, object?>
        {
            [nameof(UserPipelineHandoverDialog.Username)] = username,
            [nameof(UserPipelineHandoverDialog.Message)] = string.Format(L["DeactivateUserConfirm"].Value, username),
            [nameof(UserPipelineHandoverDialog.Delete)] = false,
            [nameof(UserPipelineHandoverDialog.DefaultTarget)] = Auth.Username,
        }, new OmniDialogOptions { Width = "560px", AutoFocusFirstElement = false }) as UserPipelineHandover;

    private async Task OnRolesChanged(UserDto user, object value)
    {
        var roles = (value as IEnumerable<string>)?.ToList() ?? [];
        var request = new UpdateUserRequest
        {
            Username = user.Username,
            Email = user.Email,
            IsActive = user.IsActive,
            // Preserve the current value - UpdateUserRequest.MustChangePassword defaults to false, so
            // omitting it here would silently clear a pending forced-change when only roles changed.
            MustChangePassword = user.MustChangePassword,
            Roles = roles
        };
        var updated = await Api.Auth.UpdateUserAsync(user.Id, request);
        if (updated is not null)
        {
            Toast.Success(L["Saved"], L["RolesUpdated"]);
        }
        else
        {
            Toast.Error(L["Error"], L["SaveFailed"]);
            await RefreshSilentlyAsync(() => _grid?.Reload() ?? Task.CompletedTask);
        }
    }

    private static OmniTone GetRoleBadgeStyle(string roleName) =>
        roleName.Equals("Admin", StringComparison.OrdinalIgnoreCase) ? OmniTone.Danger
        : roleName.Equals("Contributor", StringComparison.OrdinalIgnoreCase) ? OmniTone.Accent
        : roleName.Equals("Reader", StringComparison.OrdinalIgnoreCase) ? OmniTone.Accent
        : OmniTone.Neutral;

    public async ValueTask DisposeAsync()
    {
        if (_adminRt is not null) await _adminRt.DisposeAsync();
    }

    /// <summary>The export writes every role of the user, the cell shows three.</summary>
    private static readonly Func<UserDto, object?> RolesExport = user => string.Join(", ", user.Roles);
}
