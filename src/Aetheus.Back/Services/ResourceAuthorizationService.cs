// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Services;

/// <summary>
/// Resource authorization under the organization model of ADR-059 (R-46): a site-wide Admin reaches
/// everything; every other right comes from a role the user holds for one organization, and reaches
/// only the resources that organization owns. Membership alone grants nothing (R-45 gives every member
/// a role instead), and a resource no organization owns (an agent pool, a resource without a project)
/// is reachable by a site-wide Admin only.
/// </summary>
public sealed class ResourceAuthorizationService(IPermissionRepository permissionRepo, IMemoryCache cache, AuthzCacheEvictor evictor) : IResourceAuthorizationService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    // Every authz cache entry for a user is tied to the user's eviction token (held by the SINGLETON
    // AuthzCacheEvictor so it is shared across request scopes), so a single InvalidateRoleCache(username)
    // evicts ALL of them at once, across request scopes.

    public async Task<bool> HasPermissionAsync(ClaimsPrincipal user, ResourceType resourceType, int? resourceId, Permission required, CancellationToken ct = default)
    {
        // Fast path: the Admin role is asserted as a JWT claim, so no DB round-trip needed.
        if (user.IsInRole(RoleNames.Admin))
            return true;

        var username = user.Identity?.Name;
        return !string.IsNullOrEmpty(username)
            && await HasPermissionAsync(username, resourceType, resourceId, required, ct).ConfigureAwait(false);
    }

    public async Task<bool> HasPermissionAsync(string username, ResourceType resourceType, int? resourceId, Permission required, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(username))
            return false;

        var siteRoleIds = await GetCachedRoleIdsAsync(username, ct).ConfigureAwait(false);
        if (siteRoleIds.Count > 0
            && await permissionRepo.HasPermissionAsync(siteRoleIds, resourceType, resourceId, required, ct).ConfigureAwait(false))
            return true;

        if (!IsOrganizationOwned(resourceType))
            return false;

        var grants = await GetCachedOrganizationGrantsAsync(username, ct).ConfigureAwait(false);
        if (grants.Count == 0)
            return false;

        // A type-level check (no resource yet, e.g. "may create a project") passes when one of the
        // user's organizations grants it; the caller then checks the organization the new resource
        // goes to (CanCreateInOrganizationAsync / CanCreateInProjectAsync).
        if (resourceId is not { } id)
            return await permissionRepo.HasPermissionAsync(
                RoleIds(grants), resourceType, null, required, ct).ConfigureAwait(false);

        var organizationId = await permissionRepo.GetResourceOrganizationIdAsync(resourceType, id, ct).ConfigureAwait(false);
        if (organizationId is not { } owner)
            return false;

        var rolesInOwner = RoleIds(grants.Where(grant => grant.OrganizationId == owner));
        return rolesInOwner.Count > 0
            && await permissionRepo.HasPermissionAsync(rolesInOwner, resourceType, id, required, ct).ConfigureAwait(false);
    }

    public async Task<List<int>?> GetAccessibleResourceIdsAsync(ClaimsPrincipal user, ResourceType resourceType, Permission required, CancellationToken ct = default)
    {
        if (user.IsInRole(RoleNames.Admin))
            return null;

        var username = user.Identity?.Name;
        if (string.IsNullOrEmpty(username))
            return [];

        var accessible = new HashSet<int>();
        var siteRoleIds = await GetCachedRoleIdsAsync(username, ct).ConfigureAwait(false);
        if (siteRoleIds.Count > 0)
        {
            var sitePermitted = await permissionRepo.GetAccessibleResourceIdsAsync(siteRoleIds, resourceType, required, ct).ConfigureAwait(false);
            // null = a site-wide wildcard (the Admin role): everything.
            if (sitePermitted is null) return null;
            accessible.UnionWith(sitePermitted);
        }

        if (!IsOrganizationOwned(resourceType))
            return [.. accessible];

        var grants = await GetCachedOrganizationGrantsAsync(username, ct).ConfigureAwait(false);
        var wildcardOrganizations = new List<int>();
        foreach (var organization in grants.GroupBy(grant => grant.OrganizationId))
        {
            var permitted = await permissionRepo.GetAccessibleResourceIdsAsync(
                RoleIds(organization), resourceType, required, ct).ConfigureAwait(false);
            if (permitted is null)
            {
                wildcardOrganizations.Add(organization.Key);
                continue;
            }
            if (permitted.Count == 0) continue;
            // A role held for organization A that names a resource of organization B grants nothing:
            // keep only the named resources organization A owns.
            var owned = await permissionRepo.GetResourceIdsByOrganizationsAsync(resourceType, [organization.Key], ct).ConfigureAwait(false);
            accessible.UnionWith(permitted.Intersect(owned));
        }

        if (wildcardOrganizations.Count > 0)
            accessible.UnionWith(await permissionRepo.GetResourceIdsByOrganizationsAsync(
                resourceType, wildcardOrganizations, ct).ConfigureAwait(false));

        return [.. accessible];
    }

    public async Task<List<int>> GetUserOrganizationIdsAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var username = user.Identity?.Name;
        return string.IsNullOrEmpty(username)
            ? []
            : await GetCachedOrganizationIdsAsync(username, ct).ConfigureAwait(false);
    }

    public async Task<List<int>> GetOrganizationIdsWithPermissionAsync(
        ClaimsPrincipal user, ResourceType resourceType, Permission required, CancellationToken ct = default)
    {
        var username = user.Identity?.Name;
        if (string.IsNullOrEmpty(username) || !IsOrganizationOwned(resourceType))
            return [];
        if (user.IsInRole(RoleNames.Admin))
            return await GetCachedOrganizationIdsAsync(username, ct).ConfigureAwait(false);

        var grants = await GetCachedOrganizationGrantsAsync(username, ct).ConfigureAwait(false);
        var organizations = new List<int>();
        foreach (var organization in grants.GroupBy(grant => grant.OrganizationId))
        {
            if (await permissionRepo.HasPermissionAsync(RoleIds(organization), resourceType, null, required, ct).ConfigureAwait(false))
                organizations.Add(organization.Key);
        }
        return organizations;
    }

    public async Task<bool> CanCreateInOrganizationAsync(
        ClaimsPrincipal user, ResourceType resourceType, int organizationId, CancellationToken ct = default)
    {
        if (user.IsInRole(RoleNames.Admin))
            return true;
        var organizations = await GetOrganizationIdsWithPermissionAsync(user, resourceType, Permission.Write, ct).ConfigureAwait(false);
        return organizations.Contains(organizationId);
    }

    public async Task<bool> CanCreateInProjectAsync(
        ClaimsPrincipal user, ResourceType resourceType, int? projectId, CancellationToken ct = default)
    {
        if (user.IsInRole(RoleNames.Admin))
            return true;
        // A resource without a project belongs to no organization: only a site-wide Admin may own one.
        if (projectId is not { } project)
            return false;
        var organizationId = await permissionRepo.GetProjectOrganizationIdAsync(project, ct).ConfigureAwait(false);
        return organizationId is { } owner
            && await CanCreateInOrganizationAsync(user, resourceType, owner, ct).ConfigureAwait(false);
    }

    public async Task<bool> CanOwnAsync(
        ClaimsPrincipal user, ResourceType resourceType, int? projectId, int? environmentId, int? projectServerId,
        CancellationToken ct = default)
    {
        // Every owner named resolves to one project: an owner that does not exist, or two that name
        // different projects, is refused (RBAC audit, finding 1: never B's project with A's environment).
        var owner = projectId;
        var named = projectId is not null;
        if (environmentId is { } environment)
        {
            var scope = await permissionRepo.GetEnvironmentProjectAsync(environment, ct).ConfigureAwait(false);
            if (scope is null || (named && scope.ProjectId != owner))
                return false;
            if (!await HasPermissionAsync(user, ResourceType.Environment, environment, Permission.Write, ct).ConfigureAwait(false))
                return false;
            (owner, named) = (scope.ProjectId, true);
        }
        if (projectServerId is { } projectServer)
        {
            var scope = await permissionRepo.GetProjectServerProjectAsync(projectServer, ct).ConfigureAwait(false);
            if (scope is null || (named && scope.ProjectId != owner))
                return false;
            // There is no ResourceType.ProjectServer: its project carries the permission.
            if (!await HasPermissionAsync(user, ResourceType.Project, scope.ProjectId, Permission.Write, ct).ConfigureAwait(false))
                return false;
        }
        // The resource's own ProjectId decides where it may be created, as before: one owned by an
        // environment or a project server alone (no ProjectId) stays a site Admin's (ADR-059). The
        // owners named above only have to agree and be writable.
        return await CanCreateInProjectAsync(user, resourceType, projectId, ct).ConfigureAwait(false);
    }

    public async Task<HashSet<string>> GetUsernamesWithPermissionAsync(
        IReadOnlyCollection<string> usernames, ResourceType resourceType, int resourceId, Permission required,
        CancellationToken ct = default)
    {
        var organizationId = IsOrganizationOwned(resourceType)
            ? await permissionRepo.GetResourceOrganizationIdAsync(resourceType, resourceId, ct).ConfigureAwait(false)
            : null;
        var permitted = await permissionRepo.GetUsernamesWithPermissionAsync(
            usernames, resourceType, resourceId, organizationId, required, ct).ConfigureAwait(false);
        return new HashSet<string>(permitted, StringComparer.Ordinal);
    }

    public async Task<bool> BelongsToProjectOrganizationAsync(
        int? projectId, ResourceType resourceType, IEnumerable<int>? resourceIds, CancellationToken ct = default)
    {
        if (resourceIds is null)
            return true; // nothing is referenced, so nothing can cross organizations
        // A project-less owner belongs to no organization: it is a site Admin's own, and the caller's
        // permission on each referenced resource is checked on its own by the endpoint.
        if (projectId is not { } project)
            return true;
        var projectOrganization = await permissionRepo.GetProjectOrganizationIdAsync(project, ct).ConfigureAwait(false);
        foreach (var id in resourceIds.Distinct())
        {
            var organization = await permissionRepo.GetResourceOrganizationIdAsync(resourceType, id, ct).ConfigureAwait(false);
            // A resource of no organization (site Admin only) may serve any project; one of another
            // organization never does (M4).
            if (organization is { } owner && owner != projectOrganization)
                return false;
        }
        return true;
    }

    public async Task<bool> HasPermissionAsync(ClaimsPrincipal user, string permissionKey, int? resourceId, CancellationToken ct = default)
    {
        var definition = PermissionCatalog.Get(permissionKey);
        if (user.IsInRole(RoleNames.Admin))
            return true;
        var username = user.Identity?.Name;
        return !string.IsNullOrEmpty(username)
            && await HasPermissionAsync(username, definition.Key, resourceId, ct).ConfigureAwait(false);
    }

    public async Task<bool> HasPermissionAsync(string username, string permissionKey, int? resourceId, CancellationToken ct = default)
    {
        var definition = PermissionCatalog.Get(permissionKey);
        // A level key is the level itself: the same rows every Read/Write/Admin check reads.
        if (definition is { Level: { } level, ResourceType: { } levelType })
            return await HasPermissionAsync(username, levelType, resourceId, level, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(username))
            return false;

        // The only site-wide role honoured is Admin (GetRoleIdsForUserAsync), and it holds every key.
        if ((await GetCachedRoleIdsAsync(username, ct).ConfigureAwait(false)).Count > 0)
            return true;
        var grants = await GetCachedOrganizationGrantsAsync(username, ct).ConfigureAwait(false);
        if (grants.Count == 0)
            return false;

        int? owner;
        if (definition.ResourceType is not { } type)
            owner = resourceId; // held on the organization itself: the id names the organization
        else if (!IsOrganizationOwned(type))
            return false;
        else if (resourceId is not { } id)
            // A type-level check passes when one of the user's organizations grants the key role-wide,
            // as the level form does; the caller then checks where the resource goes.
            return await permissionRepo.HasPermissionKeyAsync(RoleIds(grants), definition.Key, null, ct).ConfigureAwait(false);
        else
            owner = await permissionRepo.GetResourceOrganizationIdAsync(type, id, ct).ConfigureAwait(false);
        if (owner is not { } organizationId)
            return false;

        var rolesInOwner = RoleIds(grants.Where(grant => grant.OrganizationId == organizationId));
        var named = definition.ResourceType is null ? null : resourceId;
        return rolesInOwner.Count > 0
            && await permissionRepo.HasPermissionKeyAsync(rolesInOwner, definition.Key, named, ct).ConfigureAwait(false);
    }

    public Task<int?> GetResourceOrganizationIdAsync(ResourceType resourceType, int resourceId, CancellationToken ct = default) =>
        IsOrganizationOwned(resourceType)
            ? permissionRepo.GetResourceOrganizationIdAsync(resourceType, resourceId, ct)
            : Task.FromResult<int?>(null);

    public async Task<IReadOnlyList<int>?> GetOrganizationsWithPermissionKeyAsync(
        ClaimsPrincipal user, string organizationKey, CancellationToken ct = default)
    {
        var definition = PermissionCatalog.Get(organizationKey);
        if (definition.ResourceType is not null)
            throw new ArgumentException($"'{organizationKey}' is not held on an organization.", nameof(organizationKey));
        if (user.IsInRole(RoleNames.Admin))
            return null;
        var username = user.Identity?.Name;
        if (string.IsNullOrEmpty(username))
            return [];

        var organizations = new List<int>();
        foreach (var organization in (await GetCachedOrganizationGrantsAsync(username, ct).ConfigureAwait(false))
                     .GroupBy(grant => grant.OrganizationId))
        {
            if (await permissionRepo.HasPermissionKeyAsync(RoleIds(organization), definition.Key, null, ct).ConfigureAwait(false))
                organizations.Add(organization.Key);
        }
        return organizations;
    }

    /// <summary>The resource types an organization owns, directly or through a project.</summary>
    private static bool IsOrganizationOwned(ResourceType type) =>
        type is not (ResourceType.AgentPool or ResourceType.Docker);

    private static List<int> RoleIds(IEnumerable<OrganizationRoleGrant> grants) =>
        grants.Select(grant => grant.RoleId).Distinct().ToList();

    private async Task<List<int>> GetCachedOrganizationIdsAsync(string username, CancellationToken ct)
    {
        var key = $"authz:orgs:{username}";
        return await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            entry.AddExpirationToken(evictor.TokenFor(username));
            return await permissionRepo.GetOrganizationIdsForUsernameAsync(username, ct).ConfigureAwait(false);
        }).ConfigureAwait(false) ?? [];
    }

    private async Task<List<OrganizationRoleGrant>> GetCachedOrganizationGrantsAsync(string username, CancellationToken ct)
    {
        var key = $"authz:orgroles:{username}";
        return await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            entry.AddExpirationToken(evictor.TokenFor(username));
            return await permissionRepo.GetOrganizationRoleGrantsAsync(username, ct).ConfigureAwait(false);
        }).ConfigureAwait(false) ?? [];
    }

    private async Task<List<int>> GetCachedRoleIdsAsync(string username, CancellationToken ct)
    {
        var key = $"authz:roles:{username}";
        return await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            entry.AddExpirationToken(evictor.TokenFor(username));
            return await permissionRepo.GetRoleIdsForUserAsync(username, ct).ConfigureAwait(false);
        }).ConfigureAwait(false) ?? [];
    }

    public void InvalidateRoleCache(string username)
    {
        if (string.IsNullOrEmpty(username)) return;

        // Cancelling the user's eviction token (via the shared singleton evictor) drops every entry
        // tied to it (site roles, organization roles, memberships) across ALL request scopes, then a
        // fresh token is handed out for subsequent reads.
        evictor.Evict(username);

        // Belt-and-suspenders for the named keys (already covered by the token above).
        cache.Remove($"authz:roles:{username}");
        cache.Remove($"authz:orgs:{username}");
        cache.Remove($"authz:orgroles:{username}");
    }
}
