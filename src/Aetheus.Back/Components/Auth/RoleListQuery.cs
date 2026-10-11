// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Auth;

/// <summary>
/// Recette R-210: the column filters of the role grids. The roles list (and the role picker) filters the
/// projected <see cref="RoleDto"/>, so the counts are compared as the grid shows them; a role's users
/// grid filters the <see cref="User"/> entity (SITE-FUNC-ROLE-001: Npgsql cannot translate a filter on the
/// positional <see cref="RoleUserDto"/>, which is projected after paging). The keys are the grids' column keys.
/// </summary>
internal static class RoleListQuery
{
    internal static readonly GridQueryMap<RoleDto> Columns = new GridQueryMap<RoleDto>()
        .Text("name", r => r.Name)
        .Text("description", r => r.Description)
        .Number("permissionCount", r => r.PermissionCount)
        .Number("userCount", r => r.UserCount);

    internal static readonly GridQueryMap<User> UserColumns = new GridQueryMap<User>()
        .Text("username", u => u.Username)
        .Text("email", u => u.Email)
        .Boolean("isActive", u => u.IsActive);
}
