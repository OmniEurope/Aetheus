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
    internal const string EmailKey = "email";

    /// <summary>The site Admin's view of every user: every column, every role.</summary>
    internal static readonly GridQueryMap<User> Columns = For(organizationIds: null, siteAdminView: true);

    /// <summary>
    /// The columns of a list limited to <paramref name="organizationIds"/> (null: every user). The Roles filter
    /// matches the roles held there only: a role of the same name held in another organization (R-2) is never
    /// matched. Outside the site Admin's view the filter neither matches the site-wide Admin role, which that view
    /// does not show, nor the e-mail column, which it masks (see <see cref="WithoutEmail"/>).
    /// </summary>
    internal static GridQueryMap<User> For(IReadOnlyList<int>? organizationIds, bool siteAdminView) =>
        new GridQueryMap<User>()
            .Text("username", u => u.Username)
            .Text(EmailKey, u => u.Email)
            .Predicate(RolesKey, filter => HoldsAnyRole(filter, organizationIds, siteAdminView))
            .Boolean("isActive", u => u.IsActive)
            .Date("createdAt", u => u.CreatedAt)
            .Date("updatedAt", u => u.UpdatedAt)
            .Date("lastActivityAt", u => u.LastActivityAt);

    /// <summary>The filters without those on the e-mail column, ignored for a caller the e-mail is masked from:
    /// a prefix filter would otherwise let it guess an address it is not shown.</summary>
    internal static IReadOnlyList<GridFilter>? WithoutEmail(IReadOnlyList<GridFilter>? filters) =>
        filters?.Where(filter => !string.Equals(filter.Field, EmailKey, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The sort key the list honours: one of its columns or the id (never a credential or security column of the entity,
    /// such as the password hash), and never the e-mail for a caller it is masked from. Null keeps the default order.
    /// </summary>
    internal static string? SortKey(string? sortBy, bool siteAdminView)
    {
        var key = sortBy?.Trim();
        if (string.IsNullOrEmpty(key) || string.Equals(key, RolesKey, StringComparison.OrdinalIgnoreCase)) return null;
        if (!siteAdminView && string.Equals(key, EmailKey, StringComparison.OrdinalIgnoreCase)) return null;
        return string.Equals(key, nameof(User.Id), StringComparison.OrdinalIgnoreCase)
            || Columns.Keys.Contains(key, StringComparer.OrdinalIgnoreCase) ? key : null;
    }

    private static Expression<Func<User, bool>> HoldsAnyRole(GridFilter filter, IReadOnlyList<int>? organizationIds, bool siteAdminView)
    {
        IReadOnlyList<string> ticked = filter.Operator switch
        {
            GridFilterOperator.In => GridQueryMap<User>.SplitList(filter.Value),
            GridFilterOperator.Equals when !string.IsNullOrWhiteSpace(filter.Value) => [filter.Value],
            _ => throw new BadRequestException($"'{filter.Field}' only accepts a list of role names.")
        };
        var names = ticked.Select(name => name.ToLowerInvariant()).ToList();
        // A site-wide Admin (site Admin's view only), or a role held for one of the listed organizations (ADR-059).
        return u => (siteAdminView && u.UserRoles.Any(userRole => userRole.Role.Name == RoleNames.Admin && names.Contains(userRole.Role.Name.ToLower())))
            || u.OrganizationRoles.Any(assignment =>
                (organizationIds == null || organizationIds.Contains(assignment.OrganizationId))
                && names.Contains(assignment.Role.Name.ToLower()));
    }
}
