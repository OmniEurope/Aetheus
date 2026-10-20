// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Users;

public partial class RoleEdit
{
    [Parameter] public int Id { get; set; }

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private ConfirmHelper Confirm { get; set; } = default!;

    private bool _isNew;
    private bool _authorized;
    private (bool IsNew, int Id)? _loadedRoute;
    private bool _loading = true;
    private bool _saving;
    private bool _savingPermissions;
    private string _name = string.Empty;
    private RoleFormModel _model = new();
    private List<PermissionRowModel> _permissionRows = [];
    // FRONT-023: the role's permissions on one resource (Server 42 Read...). The grid edits only the
    // global ones, but saving replaces the whole list, so these are sent back unchanged with it.
    private List<ResourcePermissionEntry> _scopedPermissions = [];
    private List<OmniOption<string>> _scopeOptions = [];
    private Permission? _bulkPermission;
    private List<OmniOption<Permission?>> _permissionLevels = [];

    // Users tab (lazy-loaded on activation).
    private List<RoleUserDto> _roleUsers = [];
    private List<RoleUserDto> _availableUsers = [];
    private AetheusDataGrid<RoleUserDto>? _usersGrid;
    private int _roleUsersCount;
    private int _availableUsersCount;
    private bool _usersLoaded;
    private bool _loadingRoleUsers;
    private bool _loadingAvailableUsers;
    private bool _addingUser;
    private int? _selectedUserId;
    private string? _availableUsersSearch;
    private Func<string, string>? _activeText;

    // The users grid's Status filter reads as the column does (Active / Inactive).
    private Func<string, string> ActiveText => _activeText ??= value =>
        bool.TryParse(value, out var active) ? L[active ? "Active" : "Inactive"].Value : value;

    protected override void OnInitialized()
    {
        if (!Auth.IsAdmin)
        {
            Nav.NavigateTo("/");
            return;
        }

        _authorized = true;

        _permissionLevels =
        [
            new((Permission?)Permission.Read, L["Read"].Value),
            new((Permission?)Permission.Write, L["Write"].Value),
            new((Permission?)Permission.Admin, L["Admin"].Value)
        ];
        _scopeOptions =
        [
            new("All", L["All"].Value),
            new("None", L["None"].Value)
        ];
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_authorized) return;

        var isNew = Nav.Uri.Contains("/roles/new", StringComparison.OrdinalIgnoreCase);
        var route = (IsNew: isNew, Id);
        if (_loadedRoute == route) return;
        _loadedRoute = route;
        _isNew = isNew;
        ResetLoadedState();

        if (_isNew)
        {
            Breadcrumb.Set(
                new BreadcrumbItem(L["Administration"], "/admin"),
                new BreadcrumbItem(L["Roles"], "/admin/roles"),
                new BreadcrumbItem(L["NewRole"]));
            _loading = false;
            return;
        }

        var id = Id;
        RoleDto? role;
        try { role = await Api.Auth.GetRoleAsync(id); }
        catch (HttpRequestException) { role = null; }
        if (Id != id || _isNew) return;
        if (role is null)
        {
            Nav.NavigateTo("/admin/roles");
            return;
        }

        _name = role.Name;
        _model = new RoleFormModel { Name = role.Name, Description = role.Description };

        BuildPermissionRows(role.Permissions);

        Breadcrumb.Set(
            new BreadcrumbItem(L["Administration"], "/admin"),
            new BreadcrumbItem(L["Roles"], "/admin/roles"),
            new BreadcrumbItem(role.Name));
        _loading = false;

        // Lazy-load only the users tab. EntityAuditTrail owns audit loading.
        var tab = CurrentTab();
        if (string.Equals(tab, "users", StringComparison.OrdinalIgnoreCase))
            await InitializeUsersTabAsync();
    }

    private void ResetLoadedState()
    {
        _loading = true;
        _name = string.Empty;
        _model = new RoleFormModel();
        _permissionRows = [];
        _scopedPermissions = [];
        _roleUsers = [];
        _availableUsers = [];
        _roleUsersCount = 0;
        _availableUsersCount = 0;
        _usersLoaded = false;
        _selectedUserId = null;
        _availableUsersSearch = null;
    }

    private string? CurrentTab()
    {
        var query = QueryHelpers.ParseQuery(new Uri(Nav.Uri).Query);
        return query.TryGetValue("tab", out var value) ? value.ToString() : null;
    }

    // Slugs order: 0 general, 1 permissions, 2 users, 3 audit.
    private async Task OnTabChanged(int index)
    {
        if (index == 2 && !_usersLoaded)
            await InitializeUsersTabAsync();
    }

    private async Task InitializeUsersTabAsync()
    {
        _usersLoaded = true;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>One page of the role's users export, with the grid's sort and column
    /// filters.</summary>
    private Task<PaginatedResult<RoleUserDto>> ReadRoleUsersExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(25);
        var (sortBy, sortDescending) = ResolveSort(args);
        return Api.Auth.GetRoleUsersAsync(Id, page, pageSize, sortBy: sortBy, sortDescending: sortDescending,
            filters: args.ToApiFilters());
    }

    private async Task LoadRoleUsersAsync(GridLoadArgs args)
    {
        var id = Id;
        var pageSize = args.Top ?? 25;
        var page = ((args.Skip ?? 0) / pageSize) + 1;
        var (sortBy, sortDescending) = ResolveSort(args);
        _loadingRoleUsers = true;
        try
        {
            var result = await Api.Auth.GetRoleUsersAsync(
                id, page, pageSize, sortBy: sortBy, sortDescending: sortDescending, filters: args.ToApiFilters());
            if (Id != id || _isNew) return;
            _roleUsers = result.Items;
            _roleUsersCount = result.TotalCount;
        }
        finally
        {
            if (Id == id && !_isNew)
                _loadingRoleUsers = false;
        }
    }

    private async Task LoadAvailableUsersAsync(GridLoadArgs args)
    {
        var id = Id;
        var pageSize = args.Top ?? 25;
        var page = ((args.Skip ?? 0) / pageSize) + 1;
        _availableUsersSearch = args.Filter;
        _loadingAvailableUsers = true;
        try
        {
            var result = await Api.Auth.GetUsersAvailableForRoleAsync(
                id, page, pageSize, args.Filter, "Username");
            if (Id != id || _isNew) return;
            _availableUsers = result.Items;
            _availableUsersCount = result.TotalCount;
        }
        finally
        {
            if (Id == id && !_isNew)
                _loadingAvailableUsers = false;
        }
    }

    private async Task<IReadOnlyList<OmniOption<int?>>> SearchAvailableUsersAsync(
        string search,
        CancellationToken cancellationToken)
    {
        var id = Id;
        _availableUsersSearch = search;
        _loadingAvailableUsers = true;
        try
        {
            var result = await Api.Auth.GetUsersAvailableForRoleAsync(
                id, 1, 25, search, "Username", false, cancellationToken);
            if (Id != id || _isNew) return [];
            _availableUsers = result.Items;
            _availableUsersCount = result.TotalCount;
            return result.Items
                .Select(user => new OmniOption<int?>((int?)user.UserId, user.Username))
                .ToArray();
        }
        finally
        {
            if (Id == id && !_isNew)
                _loadingAvailableUsers = false;
        }
    }

    private string FormatAvailableUser(int? userId) => _availableUsers
        .FirstOrDefault(user => user.UserId == userId)?.Username
        ?? string.Empty;

    private static (string? SortBy, bool Descending) ResolveSort(GridLoadArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy)) return ("Username", false);
        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase));
    }

    private async Task RefreshUserCollectionsAsync()
    {
        if (_usersGrid is not null)
            await _usersGrid.Reload();
        else
            await LoadRoleUsersAsync(new GridLoadArgs { Skip = 0, Top = 25 });

        await LoadAvailableUsersAsync(new GridLoadArgs
        {
            Skip = 0,
            Top = 25,
            Filter = _availableUsersSearch
        });
    }

    private async Task OnAddUser()
    {
        if (_selectedUserId is null) return;
        _addingUser = true;
        try
        {
            var status = await Api.Auth.AddUserToRoleAsync(Id, _selectedUserId.Value);
            if (status.Success)
            {
                Toast.Success("Saved");
                _selectedUserId = null;
                await RefreshUserCollectionsAsync();
            }
            else
            {
                Toast.Error("Error", "SaveFailed");
            }
        }
        finally
        {
            _addingUser = false;
        }
    }

    private async Task OnRemoveUser(RoleUserDto user)
    {
        var confirmed = await Confirm.ConfirmDeleteAsync("ConfirmRemoveUserFromRole", "Confirm", user.Username);
        if (confirmed != true) return;

        var status = await Api.Auth.RemoveUserFromRoleAsync(Id, user.UserId);
        if (status.Success)
        {
            Toast.Success("Saved");
            await RefreshUserCollectionsAsync();
        }
    }

    private void BuildPermissionRows(List<ResourcePermissionDto> permissions)
    {
        _scopedPermissions = permissions
            .Where(p => p.ResourceId is not null)
            .Select(p => new ResourcePermissionEntry
            {
                ResourceType = p.ResourceType,
                ResourceId = p.ResourceId,
                Permission = p.Permission
            })
            .ToList();
        _permissionRows = Enum.GetValues<ResourceType>().Select(rt =>
        {
            var perms = permissions.Where(p => p.ResourceType == rt && p.ResourceId is null).ToList();
            var maxPerm = perms.Count > 0 ? perms.Max(p => (int)p.Permission) : -1;
            return new PermissionRowModel
            {
                ResourceType = rt,
                Scope = maxPerm >= 0 ? "All" : "None",
                CanRead = maxPerm >= (int)Permission.Read,
                CanWrite = maxPerm >= (int)Permission.Write,
                CanAdmin = maxPerm >= (int)Permission.Admin,
            };
        }).ToList();
    }

    private void OnScopeChanged(PermissionRowModel row)
    {
        if (row.Scope == "None")
        {
            row.CanRead = false;
            row.CanWrite = false;
            row.CanAdmin = false;
        }
    }

    private void OnPermissionToggled(PermissionRowModel row)
    {
        // Enforce hierarchy: Admin ⊃ Write ⊃ Read
        if (row.CanAdmin)
        {
            row.CanWrite = true;
            row.CanRead = true;
        }
        else if (row.CanWrite)
        {
            row.CanRead = true;
        }

        row.Scope = row.CanRead || row.CanWrite || row.CanAdmin ? "All" : "None";
    }

    private async Task OnSave()
    {
        _saving = true;
        try
        {
            if (_isNew)
            {
                var result = await Api.Auth.CreateRoleAsync(new CreateRoleRequest
                {
                    Name = _model.Name,
                    Description = _model.Description
                });
                if (result is not null)
                    Nav.NavigateTo($"/admin/roles/{result.Id}");
            }
            else
            {
                var result = await Api.Auth.UpdateRoleAsync(Id, new UpdateRoleRequest
                {
                    Name = _model.Name,
                    Description = _model.Description
                });
                if (result is not null)
                {
                    _name = result.Name;
                    Toast.Success("Saved");
                }
            }
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task OnSavePermissions()
    {
        _savingPermissions = true;
        await InvokeAsync(StateHasChanged);
        try
        {
            var entries = new List<ResourcePermissionEntry>();
            foreach (var row in _permissionRows)
            {
                if (row.Scope == "None") continue;

                // Store the highest permission level (hierarchical)
                Permission maxPerm;
                if (row.CanAdmin) maxPerm = Permission.Admin;
                else if (row.CanWrite) maxPerm = Permission.Write;
                else if (row.CanRead) maxPerm = Permission.Read;
                else continue;

                entries.Add(new ResourcePermissionEntry
                {
                    ResourceType = row.ResourceType,
                    ResourceId = null,
                    Permission = maxPerm
                });
            }

            entries.AddRange(_scopedPermissions);
            var success = await Api.Auth.SetRolePermissionsAsync(Id, new SetResourcePermissionsRequest
            {
                Permissions = entries
            });

            if (success)
                Toast.Success("PermissionsSaved");
            else
                await RestorePermissionsAfterFailureAsync();
        }
        catch (HttpRequestException)
        {
            await RestorePermissionsAfterFailureAsync();
        }
        finally
        {
            _savingPermissions = false;
        }
    }

    private void OnCancel()
    {
        Nav.NavigateTo("/admin/roles");
    }

    private async Task OnApplyBulk()
    {
        if (_bulkPermission is null) return;

        foreach (var row in _permissionRows)
        {
            row.Scope = "All";
            row.CanRead = _bulkPermission.Value >= Permission.Read;
            row.CanWrite = _bulkPermission.Value >= Permission.Write;
            row.CanAdmin = _bulkPermission.Value >= Permission.Admin;
        }

        await OnSavePermissions();
    }

    private async Task RestorePermissionsAfterFailureAsync()
    {
        var role = await Api.Auth.GetRoleAsync(Id);
        if (role is not null) BuildPermissionRows(role.Permissions);
        Toast.Error("Error", "SaveFailed");
    }

    private async Task OnScopeChangedAndSaveAsync(PermissionRowModel row)
    {
        OnScopeChanged(row);
        await OnSavePermissions();
    }

    private async Task OnPermissionToggledAndSaveAsync(PermissionRowModel row)
    {
        OnPermissionToggled(row);
        await OnSavePermissions();
    }

    // The matrix is a dropdown and checkboxes; its export writes what they say.
    private Func<PermissionRowModel, object?>? _resourceTypeExport;
    private Func<PermissionRowModel, object?>? _readExport;
    private Func<PermissionRowModel, object?>? _writeExport;
    private Func<PermissionRowModel, object?>? _adminExport;
    private Func<PermissionRowModel, object?> ResourceTypeExport => _resourceTypeExport ??= row => L[row.ResourceType.ToString()].Value;
    private static readonly Func<PermissionRowModel, object?> ScopeExport = row => row.Scope;
    private Func<PermissionRowModel, object?> ReadExport => _readExport ??= row => YesNo(row.CanRead);
    private Func<PermissionRowModel, object?> WriteExport => _writeExport ??= row => YesNo(row.CanWrite);
    private Func<PermissionRowModel, object?> AdminExport => _adminExport ??= row => YesNo(row.CanAdmin);
    private string YesNo(bool value) => L[value ? "Yes" : "No"].Value;

    internal class PermissionRowModel
    {
        public ResourceType ResourceType { get; set; }
        public string Scope { get; set; } = "None";
        public bool CanRead { get; set; }
        public bool CanWrite { get; set; }
        public bool CanAdmin { get; set; }
    }

    private Func<RoleUserDto, object?> UserStatusExport => _userStatusExport ??= user => L[user.IsActive ? "Active" : "Inactive"].Value;
    private Func<RoleUserDto, object?>? _userStatusExport;
}
