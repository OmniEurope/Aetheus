// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Settings;

public partial class Administration
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;

    internal sealed record AdminTile(string Href, string Icon, string TitleKey, string DescriptionKey);

    internal sealed record AdminTileGroup(string TitleKey, IReadOnlyList<AdminTile> Tiles);

    /// <summary> Every page of the side menu's Administration group, with the icon that menu gives
    /// it (<c>NavMenu.razor</c>), so the tiles and the menu name and draw the same pages.
    /// <c>AdministrationTests</c> holds the two lists together. </summary>
    internal static IReadOnlyList<AdminTileGroup> Groups { get; } =
    [
        new("AdminGroupAccess",
        [
            new("/admin/users", "group", "Users", "UserManagement"),
            new("/admin/organizations", "apartment", "Organizations", "OrganizationManagement"),
            new("/admin/roles", "admin_panel_settings", "Roles", "RBACManagement")
        ]),
        new("Monitoring",
        [
            new("/admin/audit", "policy", "AuditLogs", "AuditLogsDescription"),
            new("/admin/system-logs", "description", "SystemLogs", "SystemLogsDescription"),
            new("/admin/performance", "speed", "Performance", "PerformanceAdminDescription"),
            new("/admin/notifications", "notifications_active", "NotificationRules", "NotificationRulesDescription"),
            new("/admin/mail-templates", "mail", "MailTemplates", "MailTemplatesDescription")
        ]),
        new("Platform",
        [
            new("/admin/settings", "tune", "PlatformSettings", "SecurityTokens"),
            new("/admin/ai-profiles", "smart_toy", "AiRunnerProfiles", "AiRunnerProfilesDescription"),
            new("/admin/package-feeds", "inventory", "PackageFeeds", "PackageFeedsDescription"),
            new("/admin/package-registry", "deployed_code", "PackageRegistry", "PackageRegistryDescription"),
            new("/admin/plugins", "extension", "Plugins", "PluginsDescription"),
            new("/admin/dashboards", "space_dashboard", "Dashboards", "ManageDashboards"),
            new("/admin/api-reference", "api", "ApiReference", "ApiReferenceDescription")
        ])
    ];

    [Inject] private PermissionService Permissions { get; set; } = default!;

    /// <summary>The groups and tiles shown: every one to a site Admin; to an organization's administrator, the
    /// pages its permissions open (owner decision 2026-10-28), a group with none of them left out.</summary>
    internal static IReadOnlyList<AdminTileGroup> VisibleGroups(bool isAdmin, PermissionService permissions) =>
        isAdmin
            ? Groups
            : [.. Groups
                .Select(group => group with
                {
                    Tiles = [.. group.Tiles.Where(tile => OrganizationAdministration.CanOpen(tile.Href, permissions))]
                })
                .Where(group => group.Tiles.Count > 0)];

    private IReadOnlyList<AdminTileGroup> Shown => VisibleGroups(Auth.IsAdmin, Permissions);

    protected override void OnInitialized()
    {
        if (!Auth.IsAdmin && !OrganizationAdministration.AdministersAny(Permissions))
        {
            Nav.NavigateTo("/");
            return;
        }

        Breadcrumb.Set(new BreadcrumbItem(L["Administration"]));
    }
}
