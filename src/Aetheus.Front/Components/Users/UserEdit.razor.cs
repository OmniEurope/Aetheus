// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Users;

public partial class UserEdit : IAsyncDisposable
{
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    private AdminEntitySubscription? _permissionChanges;

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private RealtimeSessionLifecycle RealtimeSession { get; set; } = default!;

    [Parameter] public int? Id { get; set; }

    private UserDto? _user;
    private UserModel _model = new();
    private bool _isNew => Id is null or 0;
    private bool _saving;
    private bool _savingRoles;
    private bool _savingFlags;
    private bool _rolesSaved;
    private bool _loading;
    private PasswordChangeModel _passwordChange = new();
    private bool _changingPassword;
    private const int PasswordMinLength = PasswordPolicy.MinimumLength;
    private bool PasswordMeetsRules => _passwordChange.NewPassword.Length >= PasswordMinLength;
    private int? _previousId = int.MinValue;
    // FRONT-024: bumped on every change of account so an answer requested for a previous one is dropped.
    private int _loadGeneration;

    private List<EffectivePermissionDto>? _effectivePermissions;

    // Organizations tab (lazy-loaded on activation / deep-link).
    private List<UserOrganizationDto> _userOrgs = [];
    private List<OrganizationDto> _availableOrgs = [];
    private bool _orgsLoaded;
    private bool _loadingOrgs;
    private bool _addingOrg;
    private int? _selectedOrgId;
    private OrganizationRole _newOrgRole = OrganizationRole.Member;

    private OmniDataGrid<EffectivePermissionDto>? _permissionsGrid;

    /// <summary>A grant has no id of its own: it is the permission on a resource through one role.</summary>
    private static readonly Func<EffectivePermissionDto, object> PermissionKey =
        grant => (grant.ResourceType, grant.ResourceId, grant.Permission, grant.GrantedByRole);

    // Recette R-210: the header filters show the same words as the cells, built once for stable delegates.
    private Func<string, string>? _plainText;
    private Func<string, string> PlainText => _plainText ??= value => L[value];
    private Func<string, string>? _orgRoleText;
    private Func<string, string> OrgRoleText => _orgRoleText ??= value =>
        Enum.TryParse<OrganizationRole>(value, ignoreCase: true, out var role)
            ? _orgRoleOptions.First(option => option.Value == role).Text
            : value;

    private List<OrgRoleOption> _orgRoleOptions => new()
    {
        new(OrganizationRole.Member, L["RoleMember"]),
        new(OrganizationRole.Maintainer, L["RoleMaintainer"]),
        new(OrganizationRole.Owner, L["RoleOwner"])
    };

    private record OrgRoleOption(OrganizationRole Value, string Text);

    protected override async Task OnParametersSetAsync()
    {
        if (!Auth.IsAdmin)
        {
            Nav.NavigateTo("/");
            return;
        }

        if (Id == _previousId) return;
        _previousId = Id;
        var generation = ResetForAccountChange();

        try
        {
            if (!_isNew)
            {
                _loading = true;
                var user = await Api.Auth.GetUserDetailAsync(Id!.Value);
                // FRONT-024: the route moved to another account while this one was loading.
                if (generation != _loadGeneration) return;
                _user = user;
                if (_user is not null)
                {
                    _model = new UserModel
                    {
                        Username = _user.Username,
                        Email = _user.Email,
                        IsActive = _user.IsActive,
                        MustChangePassword = _user.MustChangePassword,
                        Roles = _user.Roles.ToList()
                    };
                }
                _loading = false;
            }
        }
        catch (HttpRequestException)
        {
            // 401 on expired JWT - redirect handled by AuthProvider. Nothing of the previous account
            // is left on screen: ResetForAccountChange cleared it, so no mutation can target it.
            if (generation != _loadGeneration) return;
            _loading = false;
        }

        if (!_isNew)
        {
            await LoadEffectivePermissions();
            // Recette R-181: a user's effective permissions follow their roles, those roles'
            // permissions and their organizations; the admin hub says when any of them changed.
            if (_permissionChanges is null)
            {
                _permissionChanges = new AdminEntitySubscription(HubFactory);
                await _permissionChanges.StartAsync(
                    [AdminEntities.User, AdminEntities.Role, AdminEntities.Organization],
                    () => InvokeAsync(async () =>
                    {
                        // Recette R-227: told before the new list arrives, the grid marks the grants it did not hold.
                        if (_permissionsGrid is not null) await _permissionsGrid.RefreshAsync();
                        await LoadEffectivePermissions();
                        StateHasChanged();
                    }));
            }
        }

        Breadcrumb.Set(
            new BreadcrumbItem(L["Administration"], "/admin"),
            new BreadcrumbItem(L["Users"], "/users"),
            new BreadcrumbItem(_isNew ? L["NewUser"] : _user?.Username ?? L["User"]));

        // Deep-link straight to the organizations tab: load it up front.
        if (!_isNew && string.Equals(CurrentTab(), "organizations", StringComparison.OrdinalIgnoreCase))
            await LoadUserOrgsAsync();
    }

    /// <summary>
    /// FRONT-024: forgets everything shown for the previous account before the next one loads, so a
    /// failed or slow load never leaves account A's name above actions that target account B.
    /// </summary>
    private int ResetForAccountChange()
    {
        _user = null;
        _model = new UserModel();
        _passwordChange = new PasswordChangeModel();
        _effectivePermissions = null;
        _userOrgs = [];
        _availableOrgs = [];
        _orgsLoaded = false;
        _selectedOrgId = null;
        _rolesSaved = false;
        return ++_loadGeneration;
    }

    /// <summary>
    /// The account a destructive action may target: the one displayed, and only while the route still
    /// names it. Captured before a confirmation so the action cannot drift to another account.
    /// </summary>
    private UserDto? DisplayedTarget() => _user is { } user && user.Id == Id ? user : null;

    private string? CurrentTab()
    {
        var query = QueryHelpers.ParseQuery(new Uri(Nav.Uri).Query);
        return query.TryGetValue("tab", out var value) ? value.ToString() : null;
    }

    // Slugs order: 0 general, 1 roles, 2 organizations, 3 security, 4 audit.
    private async Task OnTabChanged(int index)
    {
        if (index == 2 && !_orgsLoaded)
            await LoadUserOrgsAsync();
    }

    private async Task LoadUserOrgsAsync()
    {
        if (_isNew || Id is null or 0) return;
        var generation = _loadGeneration;
        _loadingOrgs = true;
        await InvokeAsync(StateHasChanged);
        try
        {
            var userOrgs = await Api.Servers.GetUserOrganizationsAsync(Id.Value);
            if (generation != _loadGeneration) return;
            _userOrgs = userOrgs;
            var memberOf = _userOrgs.Select(o => o.OrganizationId).ToHashSet();
            var available = new List<OrganizationDto>();
            const int pageSize = 200;
            for (var page = 1; ; page++)
            {
                var result = await Api.Servers.GetOrganizationsAsync(null, page, pageSize);
                if (result is null) break;
                available.AddRange(result.Items.Where(o => !memberOf.Contains(o.Id)));
                if (page * pageSize >= result.TotalCount || result.Items.Count == 0) break;
            }
            if (generation != _loadGeneration) return;
            _availableOrgs = available;
            _orgsLoaded = true;
        }
        finally
        {
            _loadingOrgs = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task OnAddOrg()
    {
        if (_selectedOrgId is null || Id is null) return;
        _addingOrg = true;
        try
        {
            var member = await Api.Servers.AddOrganizationMemberAsync(_selectedOrgId.Value,
                new AddOrganizationMemberRequest { UserId = Id.Value, Role = _newOrgRole });
            if (member is not null)
            {
                Toast.Success("Saved", "Saved");
                _selectedOrgId = null;
                _newOrgRole = OrganizationRole.Member;
                await LoadUserOrgsAsync();
            }
            else
            {
                Toast.Error("Error", "SaveFailed");
            }
        }
        finally
        {
            _addingOrg = false;
        }
    }

    private async Task OnChangeOrgRole(UserOrganizationDto org, OrganizationRole role)
    {
        var updated = await Api.Servers.UpdateOrganizationMemberAsync(org.OrganizationId, org.MemberId,
            new UpdateOrganizationMemberRequest { Role = role });
        if (updated is not null)
        {
            var idx = _userOrgs.FindIndex(o => o.MemberId == org.MemberId);
            if (idx >= 0) _userOrgs[idx] = org with { Role = role };
            Toast.Success("Saved", "Saved");
        }
        else
        {
            Toast.Error("Error", "SaveFailed");
        }
    }

    private async Task OnRemoveOrg(UserOrganizationDto org)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["ConfirmRemoveFromOrganization"].Value, org.Name),
            L["Remove"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Remove"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        if (await Api.Servers.RemoveOrganizationMemberAsync(org.OrganizationId, org.MemberId))
        {
            await LoadUserOrgsAsync();
            Toast.Success("Deleted", "Saved");
        }
        else
        {
            Toast.Error("Error", "DeleteFailed");
        }
    }

    internal async Task OnSubmit()
    {
        _saving = true;
        try
        {
            if (_isNew)
            {
                if (string.IsNullOrWhiteSpace(_model.Password) || _model.Password.Length < PasswordPolicy.MinimumLength)
                {
                    Toast.Error(L["Error"], L["PasswordMinLength"]);
                    return;
                }

                var outcome = await Api.Auth.CreateUserAsync(new CreateUserRequest
                {
                    Username = _model.Username,
                    Password = _model.Password,
                    Email = string.IsNullOrWhiteSpace(_model.Email) ? null : _model.Email,
                    MustChangePassword = _model.MustChangePassword,
                    Roles = _model.Roles
                });
                if (outcome.Value is { } created)
                {
                    Toast.Success("Created", "UserCreated");
                    Nav.NavigateTo($"/users/{created.Id}");
                }
                else if (!string.IsNullOrWhiteSpace(outcome.Error?.Message))
                {
                    Toast.Notify(OmniSeverity.Danger, "Error", outcome.Error.Message);
                }
                else
                {
                    Toast.Error("Error", "SaveFailed");
                }
            }
            else
            {
                var editingCurrentUser = IsEditingCurrentUser;
                var updated = await Api.Auth.UpdateUserAsync(Id!.Value, new UpdateUserRequest
                {
                    Username = _model.Username,
                    Email = string.IsNullOrWhiteSpace(_model.Email) ? null : _model.Email,
                    IsActive = _user!.IsActive,
                    MustChangePassword = _user.MustChangePassword,
                    Roles = _model.Roles
                });
                if (updated is not null)
                {
                    _user = await Api.Auth.GetUserDetailAsync(Id!.Value);
                    await LoadEffectivePermissions();
                    if (editingCurrentUser)
                        Permissions.SetPermissions(_effectivePermissions ?? [], Auth.IsAdmin);
                    Toast.Success("Saved", "UserSaved");
                }
                else
                {
                    Toast.Error("Error", "SaveFailed");
                }
            }
        }
        finally
        {
            _saving = false;
        }
    }

    internal bool IsEditingCurrentUser =>
        string.Equals(_user?.Username, Auth.Username, StringComparison.OrdinalIgnoreCase);
    internal string? EditedUsername => _user?.Username;

    private Task OnMustChangePasswordChangedAsync(bool value) => UpdateFlagsAsync(_model.IsActive, value);

    private async Task OnActiveChangedAsync(bool value)
    {
        if (_user is null || value == _user.IsActive) return;
        if (!value)
        {
            var confirmed = await Dialog.Confirm(
                string.Format(L["DeactivateUserConfirm"].Value, _user.Username),
                L["DeactivateUser"].Value,
                new OmniConfirmOptions
                {
                    Destructive = true,
                    OkButtonText = L["Deactivate"].Value,
                    CancelButtonText = L["GoBack"].Value
                });
            if (confirmed != true) return;
        }

        await UpdateFlagsAsync(value, _model.MustChangePassword);
    }

    private async Task UpdateFlagsAsync(bool isActive, bool mustChangePassword)
    {
        if (_isNew || Id is null or 0 || _user is null || _savingFlags) return;
        var previousActive = _model.IsActive;
        var previousMustChange = _model.MustChangePassword;
        var editingCurrentUser = IsEditingCurrentUser;
        _model.IsActive = isActive;
        _model.MustChangePassword = mustChangePassword;
        _savingFlags = true;
        try
        {
            var updated = await Api.Auth.UpdateUserAsync(Id.Value, new UpdateUserRequest
            {
                Username = _user.Username,
                Email = _user.Email,
                IsActive = isActive,
                MustChangePassword = mustChangePassword,
                Roles = _user.Roles
            });
            if (updated is null)
            {
                _model.IsActive = previousActive;
                _model.MustChangePassword = previousMustChange;
                Toast.Error("Error", "SaveFailed");
                return;
            }

            _user = updated;
            _model.IsActive = updated.IsActive;
            _model.MustChangePassword = updated.MustChangePassword;
            Toast.Success("Saved", "UserSaved");

            if (editingCurrentUser && !updated.IsActive)
            {
                await RealtimeSession.StopAsync();
                await Auth.LogoutAsync();
                Nav.NavigateTo(LoginRedirect.ToLogin(Nav));
            }
        }
        catch (HttpRequestException)
        {
            _model.IsActive = previousActive;
            _model.MustChangePassword = previousMustChange;
            Toast.Error("Error", "SaveFailed");
        }
        finally
        {
            _savingFlags = false;
        }
    }

    private async Task OnDelete()
    {
        if (DisplayedTarget() is not { } target) return;
        var confirmed = await Dialog.Confirm(L["DeleteUserConfirm"].Value, L["DeleteUser"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        // FRONT-024: the confirmed account, still the one on screen, and nobody else.
        if (confirmed == true && DisplayedTarget()?.Id == target.Id)
        {
            var status = await Api.Auth.DeleteUserAsync(target.Id);
            if (status.Success)
            {
                Toast.Success("Deleted", "Deleted");
                Nav.NavigateTo("/users");
            }
            else
                Toast.Error("Error", "DeleteFailed");
        }
    }

    private async Task OnChangePassword(PasswordChangeModel model)
    {
        // The button is always clickable; validate here and tell the user exactly why it failed
        // instead of silently doing nothing.
        if (!PasswordMeetsRules)
        {
            Toast.Warning("Error", "PasswordMinLength");
            return;
        }

        if (DisplayedTarget() is not { } target) return;
        _changingPassword = true;
        try
        {
            var status = await Api.Auth.ChangeUserPasswordAsync(target.Id, new ChangeUserPasswordRequest { NewPassword = model.NewPassword });
            if (status.Success)
            {
                Toast.Success("Saved", "PasswordChanged");
                _passwordChange = new PasswordChangeModel();
            }
            else
            {
                Toast.Error("Error", "PasswordChangeFailed");
            }
        }
        finally
        {
            _changingPassword = false;
        }
    }

    private void OnInvalidPasswordSubmit(EditContext _)
    {
        Toast.Warning("Error", "PasswordMinLength");
    }

    private async Task OnRolesChangedAsync(List<string> roles)
    {
        if (_isNew || Id is null or 0 || _user is null || _savingRoles) return;

        var previousRoles = _user.Roles.ToList();
        var editingCurrentUser = IsEditingCurrentUser;
        _model.Roles = roles.ToList();
        _rolesSaved = false;
        _savingRoles = true;
        try
        {
            // Roles save independently so unsaved edits on the General tab are never submitted
            // as a side effect of a checkbox click.
            var updated = await Api.Auth.UpdateUserAsync(Id.Value, new UpdateUserRequest
            {
                Username = _user.Username,
                Email = _user.Email,
                IsActive = _user.IsActive,
                MustChangePassword = _user.MustChangePassword,
                Roles = _model.Roles
            });
            if (updated is null)
            {
                _model.Roles = previousRoles;
                Toast.Error("Error", "SaveFailed");
                return;
            }

            _user = updated;
            _model.Roles = updated.Roles.ToList();
            _rolesSaved = true;
            Toast.Success("Saved", "UserSaved");

            try
            {
                await LoadEffectivePermissions();
                if (editingCurrentUser)
                    Permissions.SetPermissions(_effectivePermissions ?? [], Auth.IsAdmin);
            }
            catch (HttpRequestException ex)
            {
                // The role update already succeeded. In particular, changing the current user's roles
                // rotates their security stamp, so the follow-up permissions read can legitimately be 401.
                _effectivePermissions = null;
                Toast.Warning(
                    "Saved",
                    editingCurrentUser && ex.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        ? "UserRolesSavedPermissionRefreshUnauthorized"
                        : "UserRolesSavedPermissionRefreshFailed");
            }
        }
        catch (HttpRequestException)
        {
            _model.Roles = previousRoles;
            Toast.Error("Error", "SaveFailed");
        }
        finally
        {
            _savingRoles = false;
        }
    }

    private async Task LoadEffectivePermissions()
    {
        if (_isNew || Id is null or 0) return;

        var generation = _loadGeneration;
        var summary = await Api.Auth.GetUserEffectivePermissionsAsync(Id!.Value);
        if (generation != _loadGeneration) return;
        _effectivePermissions = summary?.EffectivePermissions ?? [];
    }

    private static OmniTone GetPermissionBadgeStyle(Permission permission) => permission switch
    {
        Permission.Admin => OmniTone.Danger,
        Permission.Write => OmniTone.Warning,
        _ => OmniTone.Accent
    };

    public async ValueTask DisposeAsync()
    {
        if (_permissionChanges is not null)
            await _permissionChanges.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private class UserModel
    {
        [Required(ErrorMessage = "UsernameRequired")]
        [StringLength(50, ErrorMessage = "UsernameTooLong")]
        public string Username { get; set; } = string.Empty;

        [StringLength(200)]
        public string? Email { get; set; }

        [StringLength(100)]
        public string Password { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;
        public bool MustChangePassword { get; set; } = true;
        public List<string> Roles { get; set; } = [];
    }

    private sealed class PasswordChangeModel
    {
        [Required, StringLength(100, MinimumLength = PasswordMinLength)]
        public string NewPassword { get; set; } = string.Empty;
    }
}
