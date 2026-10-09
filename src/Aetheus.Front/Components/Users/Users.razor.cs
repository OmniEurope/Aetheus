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
    [Inject] private AdminIdentitySearch Search { get; set; } = default!;

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
        if (!AdminPageEntry.TryEnter(Auth, Nav, Breadcrumb, L, "Users"))
            return;

        // Recette R-316: the search shared with Organizations and Roles, in the section bar.
        Search.Changed += OnSharedSearchChangedAsync;
        // Realtime: reload the grid whenever any session creates/updates/deletes a user (the creator
        // also receives this broadcast, so the list refreshes even within the acting session). RT4M.
        _adminRt = new AdminEntitySubscription(HubFactory);
        await _adminRt.StartAsync(AdminEntities.User,
            () => InvokeAsync(async () =>
            {
                Cache.InvalidatePrefix("users:");
                // Recette R-226: a change pushed by the hub refreshes the grid quietly (rows, page, filters kept).
                await RefreshSilentlyAsync(() => _grid?.Refresh() ?? Task.CompletedTask);
            }));
        // Stale-while-revalidate: pre-seed the grid from the cached default view (grid PageSize = 20)
        // so revisiting /admin/users paints instantly; OnLoadData then revalidates in the background.
        Cache.Seed<PaginatedResult<UserDto>>(CacheKey(1, 20, Search.Search), ApplyUsers);
        // The shared grid requests its first page after its columns are registered.
        await LoadRoleNamesAsync();
    }

    // Recette R-210: the role names the Roles header filter offers (every role, not only those on screen).
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
    private string CacheKey(int page, int pageSize, string? search, string? sortBy = null, bool sortDescending = false) =>
        $"users:{page}:{pageSize}:{search}:{sortBy}:{sortDescending}:{GridColumnFilters.CacheText(_columnFilters)}";

    private void ApplyUsers(PaginatedResult<UserDto> result)
    {
        _users = result.Items;
        _totalCount = result.TotalCount;
    }

    /// <summary>Recette R2-124: one page of the export, with the search, sort and column filters of the grid.</summary>
    private Task<PaginatedResult<UserDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(UserDto.Username));
        return Api.Auth.GetUsersAsync(page, pageSize, Search.Search, sortBy, sortDescending, args.ToApiFilters());
    }

    private async Task OnLoadData(GridLoadArgs args)
    {
        _loadFailed = false;
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(UserDto.Username));
        // Recette R-210 / R-224: the header filters, applied by the API (and part of the cache key).
        _columnFilters = args.ToApiFilters();
        var filters = _columnFilters;
        var search = Search.Search;
        var showLoading = _silentRefreshDepth == 0;
        await Cache.RevalidateAsync(
            CacheKey(page, pageSize, search, sortBy, sortDescending),
            () => Api.Auth.GetUsersAsync(page, pageSize, search, sortBy, sortDescending, filters),
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

    private async Task ResetAndReload()
    {
        if (_grid is not null) await _grid.GoToPage(0);
    }

    // Recette R-316: a new term reloads the first page even when the grid is already on it (a plain
    // GoToPage(0) is a no-op there, which left the list unfiltered while the counts moved).
    private Task OnSharedSearchChangedAsync() => _grid?.GoToPage(0, forceReload: true) ?? Task.CompletedTask;

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

    private async Task OnCreate()
    {
        var created = await Dialog.OpenAsync<UserCreateDialog>(L["NewUser"],
            new Dictionary<string, object?>(),
            new OmniDialogOptions { Width = "560px", CloseDialogOnOverlayClick = false, AutoFocusFirstElement = false });

        if (created is UserDto)
            await RefreshSilentlyAsync(ResetAndReload);
    }

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
        Search.Changed -= OnSharedSearchChangedAsync;
        if (_adminRt is not null) await _adminRt.DisposeAsync();
    }

    /// <summary>Recette R2-124: the export writes every role of the user, the cell shows three.</summary>
    private static readonly Func<UserDto, object?> RolesExport = user => string.Join(", ", user.Roles);
}
