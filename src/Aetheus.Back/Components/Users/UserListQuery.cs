// SPDX-License-Identifier: EUPL-1.2
using System.Linq.Expressions;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Users;

/// <summary> The column filters of the users list. The keys are the grid's column keys; the Roles
/// column is a checkable list of role names, a user matching when it holds any ticked role.
/// </summary>
internal static class UserListQuery
{
    internal const string RolesKey = "roles";

    internal static readonly GridQueryMap<User> Columns = new GridQueryMap<User>()
        .Text("username", u => u.Username)
        .Text("email", u => u.Email)
        .Predicate(RolesKey, HoldsAnyRole)
        .Boolean("isActive", u => u.IsActive)
        .Date("createdAt", u => u.CreatedAt);

    private static Expression<Func<User, bool>> HoldsAnyRole(GridFilter filter)
    {
        IReadOnlyList<string> ticked = filter.Operator switch
        {
            GridFilterOperator.In => GridQueryMap<User>.SplitList(filter.Value),
            GridFilterOperator.Equals when !string.IsNullOrWhiteSpace(filter.Value) => [filter.Value],
            _ => throw new BadRequestException($"'{filter.Field}' only accepts a list of role names.")
        };
        var names = ticked.Select(name => name.ToLowerInvariant()).ToList();
        return u => u.UserRoles.Any(userRole => names.Contains(userRole.Role.Name.ToLower()));
    }
}
