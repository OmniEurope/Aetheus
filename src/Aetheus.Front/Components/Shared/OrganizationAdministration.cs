// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Which pages of the Administration section an organization's administrator opens (owner decision
/// 2026-10-28): the users and organizations with org.members.manage, the roles with org.roles.manage, the
/// mail templates with org.mailtemplates.manage, the section's hub with any of them. A site Admin opens
/// every page. Like the other client checks, this drives what the UI offers; the backend decides.
/// </summary>
public static class OrganizationAdministration
{
    /// <summary>True when the user administers at least one organization (holds one of the keys somewhere).</summary>
    public static bool AdministersAny(PermissionService permissions) =>
        PermissionCatalog.OrganizationAdministrationKeys.Any(key => permissions.Can(key));

    /// <summary>The key an organization's administrator needs for the Administration page at
    /// <paramref name="path"/> (no leading slash); null for a page reserved to the site Admin.</summary>
    public static string? KeyFor(string path)
    {
        var segments = path.Split('?', '#')[0].Trim('/').Split('/');
        if (segments.Length < 2 || !segments[0].Equals("admin", StringComparison.OrdinalIgnoreCase)) return null;
        var section = segments[1].ToLowerInvariant();
        var tail = segments.Skip(2).ToArray();
        return section switch
        {
            // The list, and creating an account in its organizations (R-10 d); an account's page stays the site Admin's.
            "users" when tail.Length == 0 || (tail.Length == 1 && tail[0] == "new") => PermissionKeys.OrganizationMembersManage,
            // The list and one organization's page (its members); creating, editing or deleting an
            // organization stays the site Admin's.
            "organizations" when tail.Length == 0 || (tail.Length == 1 && int.TryParse(tail[0], out _)) =>
                PermissionKeys.OrganizationMembersManage,
            "roles" when tail.Length == 0 || (tail.Length == 1 && (int.TryParse(tail[0], out _) || tail[0] == "new")) =>
                PermissionKeys.OrganizationRolesManage,
            "mail-templates" when tail.Length == 0 => PermissionKeys.OrganizationMailTemplatesManage,
            _ => null
        };
    }

    /// <summary>True when a user who is not a site Admin may open <paramref name="path"/>.</summary>
    public static bool CanOpen(string path, PermissionService permissions)
    {
        var trimmed = path.Split('?', '#')[0].Trim('/');
        if (trimmed.Equals("admin", StringComparison.OrdinalIgnoreCase)) return AdministersAny(permissions);
        return KeyFor(trimmed) is { } key && permissions.Can(key);
    }
}
