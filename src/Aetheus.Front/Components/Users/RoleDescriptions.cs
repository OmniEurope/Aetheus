// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Users;

/// <summary>
/// Localizes well-known seeded role descriptions (F-24), shared by the roles list and the role picker.
/// Falls back to the raw value for user-created roles.
/// </summary>
public static class RoleDescriptions
{
    public static string Localize(RoleDto role, IStringLocalizer<AppStrings> l) => role.Name switch
    {
        "Admin" => l["RoleAdminDescription"],
        RoleNames.OrganizationAdmin => l["RoleOrganizationAdminDescription"],
        "Reader" => l["RoleReaderDescription"],
        "Contributor" => l["RoleContributorDescription"],
        _ => role.Description ?? string.Empty
    };
}
