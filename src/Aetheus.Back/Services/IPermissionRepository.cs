// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Services;

/// <summary>A role a user holds for one organization (ADR-059).</summary>
public sealed record OrganizationRoleGrant(int OrganizationId, int RoleId, string RoleName);

public interface IPermissionRepository
{
    /// <summary>
    /// The site-wide role ids of the user: only the Admin role is honoured site-wide (ADR-059), so
    /// this is either empty or the Admin role.
    /// </summary>
    Task<List<int>> GetRoleIdsForUserAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Every role the user holds for an organization it is still a member of.
    /// </summary>
    Task<List<OrganizationRoleGrant>> GetOrganizationRoleGrantsAsync(string username, CancellationToken ct = default);

    Task<bool> HasPermissionAsync(List<int> roleIds, ResourceType resourceType, int? resourceId, Permission required, CancellationToken ct = default);
    /// <summary>
    /// R-11: true when one of <paramref name="roleIds"/> holds the action key role-wide, or on
    /// <paramref name="resourceId"/> (a null <paramref name="resourceId"/> asks for a role-wide grant).
    /// </summary>
    Task<bool> HasPermissionKeyAsync(List<int> roleIds, string permissionKey, int? resourceId, CancellationToken ct = default);

    Task<List<int>?> GetAccessibleResourceIdsAsync(List<int> roleIds, ResourceType resourceType, Permission required, CancellationToken ct = default);

    /// <summary>
    /// Returns the ids of every organization the user is a direct member of.
    /// </summary>
    Task<List<int>> GetOrganizationIdsForUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Returns the ids of the resources of the given <paramref name="resourceType"/> owned by one of
    /// <paramref name="orgIds"/>, directly (Server, Project, PipelineTemplate) or through their project
    /// (Pipeline, Release, Environment, VariableLibrary, Vault, ServiceConnection). Empty for the types
    /// no organization owns (AgentPool, Docker) and for resources without a project.
    /// </summary>
    Task<List<int>> GetResourceIdsByOrganizationsAsync(ResourceType resourceType, List<int> orgIds, CancellationToken ct = default);

    /// <summary>
    /// The organization that owns the resource, or null when it does not exist or no organization owns
    /// it (an AgentPool, or a resource without a project).
    /// </summary>
    Task<int?> GetResourceOrganizationIdAsync(ResourceType resourceType, int resourceId, CancellationToken ct = default);

    /// <summary>The organization of the project, or null when the project does not exist.</summary>
    Task<int?> GetProjectOrganizationIdAsync(int projectId, CancellationToken ct = default);

    /// <summary>The project of the environment (its <see cref="OwnerProject.ProjectId"/> is null for an
    /// environment of no project), or null when the environment does not exist.</summary>
    Task<OwnerProject?> GetEnvironmentProjectAsync(int environmentId, CancellationToken ct = default);

    /// <summary>The project of the project server, or null when the project server does not exist.</summary>
    Task<OwnerProject?> GetProjectServerProjectAsync(int projectServerId, CancellationToken ct = default);

    /// <summary>
    /// The usernames among <paramref name="usernames"/> that hold <paramref name="required"/> on the resource,
    /// in one query: active users holding the site-wide Admin role, or a role held for
    /// <paramref name="organizationId"/> (the resource's owner) while still members of it.
    /// </summary>
    Task<List<string>> GetUsernamesWithPermissionAsync(
        IReadOnlyCollection<string> usernames, ResourceType resourceType, int resourceId, int? organizationId,
        Permission required, CancellationToken ct = default);
}

/// <summary>The project a scope (environment, project server) belongs to; null for a scope of no project.</summary>
public sealed record OwnerProject(int? ProjectId);
