// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Auth;

/// <summary>Map role-grid filters to RoleDto and role-user filters to User before paging, since
/// Npgsql cannot translate those filters on positional RoleUserDto projections.</summary>
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
