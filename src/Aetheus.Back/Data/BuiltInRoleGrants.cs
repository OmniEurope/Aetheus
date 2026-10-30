// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Data;

/// <summary>
/// The action permissions (R-11) a built-in role holds on a fresh install, role-wide. Its level keys are
/// its <see cref="ResourcePermission"/> rows, seeded beside; the site Admin holds every action key.
/// </summary>
internal static class BuiltInRoleGrants
{
    public static IEnumerable<RolePermissionGrant> For(int roleId, string roleName) =>
        PermissionCatalog.DefaultKeysOf(roleName)
            .Where(key => !PermissionCatalog.Get(key).IsLevel)
            .Select(key => new RolePermissionGrant { RoleId = roleId, PermissionKey = key });
}
