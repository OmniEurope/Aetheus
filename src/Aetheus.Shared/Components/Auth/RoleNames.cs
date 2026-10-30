// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Shared;

namespace Aetheus.Shared.Components.Auth;

/// <summary>
/// The built-in roles (ADR-059). <see cref="Admin"/> is the only site-wide role, held for every
/// organization; every other role is held for one organization and grants its permissions on that
/// organization's resources only.
/// </summary>
public static class RoleNames
{
    /// <summary>Full access to every organization (site administrator).</summary>
    public const string Admin = "Admin";

    /// <summary>Full access to one organization's resources, and management of its members.</summary>
    public const string OrganizationAdmin = "Organization Admin";

    /// <summary>Read and write access to one organization's resources.</summary>
    public const string Contributor = "Contributor";

    /// <summary>Read access to one organization's resources.</summary>
    public const string Reader = "Reader";

    /// <summary>The roles the organization assignment model relies on by name; never renamed nor deleted.</summary>
    public static readonly IReadOnlyList<string> BuiltIn = [Admin, OrganizationAdmin, Contributor, Reader];

    /// <summary>The built-in roles held for one organization (every built-in role but the site-wide Admin): those an
    /// organization's administrator gives, with its organization's own roles (R-10 d).</summary>
    public static readonly IReadOnlyList<string> BuiltInForOneOrganization = [OrganizationAdmin, Contributor, Reader];

    /// <summary>
    /// The role a membership level gives automatically (R-45): Member is Reader, Maintainer is
    /// Contributor, Owner is Organization Admin. A member is never left without rights.
    /// </summary>
    public static string ForMembership(OrganizationRole membership) => membership switch
    {
        OrganizationRole.Owner => OrganizationAdmin,
        OrganizationRole.Maintainer => Contributor,
        _ => Reader
    };

    /// <summary>
    /// The membership level a role assignment made from the user's side gives (R-47), so that
    /// assigning Contributor to a user and adding the user as Maintainer give the same result.
    /// A custom role makes a plain member.
    /// </summary>
    public static OrganizationRole MembershipFor(string roleName) => roleName switch
    {
        OrganizationAdmin => OrganizationRole.Owner,
        Contributor => OrganizationRole.Maintainer,
        _ => OrganizationRole.Member
    };
}
