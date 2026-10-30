// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;

namespace Aetheus.Back.Services;

/// <summary>
/// The organizations an Administration page reads for its caller (owner decision 2026-10-28): every
/// organization for a site Admin (<see cref="OrganizationIds"/> null), else the organizations where the
/// caller holds the page's <c>org.*</c> key, narrowed to the one the organization switcher chose.
/// </summary>
public sealed record AdministrationScope(IReadOnlyList<int>? OrganizationIds)
{
    /// <summary>True for a site Admin with no organization chosen: nothing is filtered.</summary>
    public bool IsEverything => OrganizationIds is null;

    public bool Includes(int organizationId) => OrganizationIds is null || OrganizationIds.Contains(organizationId);

    /// <summary>
    /// The scope of <paramref name="user"/> for <paramref name="organizationKey"/>, narrowed to
    /// <paramref name="requestedOrganizationId"/> when the switcher chose one. Null (the caller answers 403)
    /// when the user administers no organization with that key, or not the one requested.
    /// </summary>
    public static async Task<AdministrationScope?> ResolveAsync(
        IResourceAuthorizationService authorization, ClaimsPrincipal user, string organizationKey,
        int? requestedOrganizationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var administered = await authorization.GetOrganizationsWithPermissionKeyAsync(user, organizationKey, ct).ConfigureAwait(false);
        if (requestedOrganizationId is { } requested)
            return administered is null || administered.Contains(requested) ? new AdministrationScope([requested]) : null;
        if (administered is null)
            return new AdministrationScope((IReadOnlyList<int>?)null);
        return administered.Count == 0 ? null : new AdministrationScope(administered);
    }
}
