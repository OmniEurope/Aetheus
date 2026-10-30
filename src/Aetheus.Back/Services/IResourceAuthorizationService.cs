// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;

namespace Aetheus.Back.Services;

public interface IResourceAuthorizationService
{
    Task<bool> HasPermissionAsync(ClaimsPrincipal user, ResourceType resourceType, int? resourceId, Permission required, CancellationToken ct = default);

    /// <summary>
    /// F-EXEC-1b: permission check for a principal identified by username only (no
    /// <see cref="ClaimsPrincipal"/>). Used to authorize non-interactive pipeline runs
    /// (webhook / scheduler) against the pipeline owner. The Admin role carries explicit
    /// <see cref="Permission.Admin"/> rows for every <see cref="ResourceType"/> (seeded by
    /// DbInitializer), so admin owners resolve correctly without an IsInRole shortcut. Every other
    /// right comes from a role held for the organization that owns the resource (ADR-059).
    /// </summary>
    Task<bool> HasPermissionAsync(string username, ResourceType resourceType, int? resourceId, Permission required, CancellationToken ct = default);

    Task<List<int>?> GetAccessibleResourceIdsAsync(ClaimsPrincipal user, ResourceType resourceType, Permission required, CancellationToken ct = default);

    /// <summary>
    /// Organization ids the user belongs to (empty when none / unauthenticated). Used by the hubs to
    /// subscribe non-admin members to the per-org aggregate groups so they receive realtime
    /// Created/Registered events for org-scoped resources that did not exist when they connected.
    /// </summary>
    Task<List<int>> GetUserOrganizationIdsAsync(ClaimsPrincipal user, CancellationToken ct = default);

    /// <summary>
    /// The organizations where a role the user holds grants <paramref name="required"/> on every
    /// resource of <paramref name="resourceType"/> (ADR-059). Used to subscribe a user to the per-org
    /// realtime groups and to pick the organization a new resource may go to. A site-wide Admin gets
    /// the organizations it is a member of; callers that need every organization test the Admin first.
    /// </summary>
    Task<List<int>> GetOrganizationIdsWithPermissionAsync(
        ClaimsPrincipal user, ResourceType resourceType, Permission required, CancellationToken ct = default);

    /// <summary>True when the user may create a resource of <paramref name="resourceType"/> owned by
    /// <paramref name="organizationId"/>: a site-wide Admin, or a role held for that organization that
    /// grants Write on every resource of the type.</summary>
    Task<bool> CanCreateInOrganizationAsync(
        ClaimsPrincipal user, ResourceType resourceType, int organizationId, CancellationToken ct = default);

    /// <summary>True when the user may give a resource of <paramref name="resourceType"/> to
    /// <paramref name="projectId"/> (create it there, or move it there): the project's organization must
    /// allow the creation. A null project (a resource no organization owns) is for a site-wide Admin only.</summary>
    Task<bool> CanCreateInProjectAsync(
        ClaimsPrincipal user, ResourceType resourceType, int? projectId, CancellationToken ct = default);

    /// <summary>
    /// True when the user may give a resource of <paramref name="resourceType"/> the owner the request names:
    /// a project, an environment or a project server (RBAC audit, findings 1 and 8). The owners named must
    /// exist and resolve to one project; an environment needs Environment Write, a project server Write on
    /// its project, and the resolved project must accept the creation (<see cref="CanCreateInProjectAsync"/>,
    /// so an owner of no project stays a site-wide Admin's).
    /// </summary>
    Task<bool> CanOwnAsync(
        ClaimsPrincipal user, ResourceType resourceType, int? projectId, int? environmentId, int? projectServerId,
        CancellationToken ct = default);

    /// <summary>
    /// The usernames among <paramref name="usernames"/> holding <paramref name="required"/> on one resource, in
    /// one query (RBAC audit, finding 12): the same verdict as <see cref="HasPermissionAsync(string, ResourceType, int?, Permission, CancellationToken)"/>
    /// per user, without one round-trip per user.
    /// </summary>
    Task<HashSet<string>> GetUsernamesWithPermissionAsync(
        IReadOnlyCollection<string> usernames, ResourceType resourceType, int resourceId, Permission required,
        CancellationToken ct = default);

    /// <summary>M4: true when none of <paramref name="resourceIds"/> belongs to an organization other than
    /// the one owning <paramref name="projectId"/>, so a write never links a resource of another
    /// organization. It checks ownership only; the caller's permission on each id is checked apart.</summary>
    Task<bool> BelongsToProjectOrganizationAsync(
        int? projectId, ResourceType resourceType, IEnumerable<int>? resourceIds, CancellationToken ct = default);

    /// <summary>
    /// R-11: true when the user holds the named permission <paramref name="permissionKey"/> (a key of
    /// <see cref="PermissionCatalog"/>) on <paramref name="resourceId"/>, a resource of the key's resource
    /// type, or on the organization <paramref name="resourceId"/> for a key held on the organization
    /// (<see cref="PermissionKeys.OrganizationMembersManage"/>). A site-wide Admin holds every key; any
    /// other holder gets it from a role held for the organization owning the resource (ADR-059). A level
    /// key is the level check of its type, so a role assembled from keys is honoured by both forms. An
    /// unknown key throws: it is a programming error, never a refusal.
    /// </summary>
    Task<bool> HasPermissionAsync(ClaimsPrincipal user, string permissionKey, int? resourceId, CancellationToken ct = default);

    /// <summary>
    /// The organizations where the user holds <paramref name="organizationKey"/>, a key held on the organization
    /// itself (<see cref="PermissionKeys.OrganizationMembersManage"/> and the other <c>org.*</c> keys): what an
    /// organization's administrator may administer. Null for a site-wide Admin, who administers every
    /// organization; empty for a user administering none. A key of a resource type throws.
    /// </summary>
    Task<IReadOnlyList<int>?> GetOrganizationsWithPermissionKeyAsync(
        ClaimsPrincipal user, string organizationKey, CancellationToken ct = default);

    /// <summary>The organization owning the resource, directly or through its project; null when none does
    /// (or the resource does not exist). An organization's own role may name only its organization's resources.</summary>
    Task<int?> GetResourceOrganizationIdAsync(ResourceType resourceType, int resourceId, CancellationToken ct = default);

    /// <summary>The named-permission check (R-11) of a principal identified by username only, as the
    /// non-interactive runs do: the same verdict as the <see cref="ClaimsPrincipal"/> form.</summary>
    Task<bool> HasPermissionAsync(string username, string permissionKey, int? resourceId, CancellationToken ct = default);

    /// <summary>
    /// Removes the cached role-id list for the given user. Must be called whenever a user's roles
    /// change or their SecurityStamp is rotated, otherwise stale role assignments grant access for
    /// up to the cache TTL.
    /// </summary>
    void InvalidateRoleCache(string username);
}
