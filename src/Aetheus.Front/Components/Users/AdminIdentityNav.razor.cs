// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Users;

public partial class AdminIdentityNav
{
    public const string UsersSection = "users";
    public const string OrganizationsSection = "organizations";
    public const string RolesSection = "roles";

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;

    [Parameter, EditorRequired] public string ActiveSection { get; set; } = string.Empty;

    /// <summary>Raised when the organization switcher's choice changed: the page reloads its list.</summary>
    [Parameter] public EventCallback OrganizationChanged { get; set; }

    /// <summary>The key the active page's API resolves its scope with.</summary>
    private string SwitcherKey => ActiveSection == RolesSection
        ? PermissionKeys.OrganizationRolesManage
        : PermissionKeys.OrganizationMembersManage;

    /// <summary>The route of a section's page.</summary>
    internal static string RouteOf(string section) => $"/admin/{section}";

    /// <summary>A site Admin sees every tab; an organization's administrator, those its permissions open.</summary>
    private bool Shows(string section) =>
        Auth.IsAdmin || OrganizationAdministration.CanOpen(RouteOf(section), Permissions);

    private void OnTabChanged(string? section)
    {
        if (string.IsNullOrEmpty(section) || section.Equals(ActiveSection, StringComparison.Ordinal))
            return;
        Nav.NavigateTo(RouteOf(section));
    }
}
