// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Services;

public class PermissionRepository(AppDbContext db) : IPermissionRepository
{
    public async Task<List<int>> GetRoleIdsForUserAsync(string username, CancellationToken ct = default)
    {
        // ADR-059: a site-wide assignment is honoured for the Admin role only. Any other role held
        // without an organization (a row left from before the organization model) grants nothing.
        return await db.Users
            .AsNoTracking()
            .Where(u => u.Username == username && u.IsActive)
            .SelectMany(u => u.UserRoles)
            .Where(ur => ur.Role.Name == RoleNames.Admin)
            .Select(ur => ur.RoleId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<OrganizationRoleGrant>> GetOrganizationRoleGrantsAsync(string username, CancellationToken ct = default)
    {
        // An organization role counts only while the user is still a member of that organization.
        return await db.UserOrganizationRoles
            .AsNoTracking()
            .Where(assignment => assignment.User.Username == username && assignment.User.IsActive
                && db.OrganizationMembers.Any(member =>
                    member.UserId == assignment.UserId && member.OrganizationId == assignment.OrganizationId))
            .Select(assignment => new OrganizationRoleGrant(assignment.OrganizationId, assignment.RoleId, assignment.Role.Name))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> HasPermissionAsync(List<int> roleIds, ResourceType resourceType, int? resourceId, Permission required, CancellationToken ct = default)
    {
        return await db.ResourcePermissions
            .AsNoTracking()
            .AnyAsync(rp =>
                roleIds.Contains(rp.RoleId)
                && rp.ResourceType == resourceType
                && (rp.ResourceId == null || rp.ResourceId == resourceId)
                && rp.Permission >= required, ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> HasPermissionKeyAsync(List<int> roleIds, string permissionKey, int? resourceId, CancellationToken ct = default)
    {
        return await db.RolePermissionGrants
            .AsNoTracking()
            .AnyAsync(grant =>
                roleIds.Contains(grant.RoleId)
                && grant.PermissionKey == permissionKey
                && (grant.ResourceId == null || grant.ResourceId == resourceId), ct)
            .ConfigureAwait(false);
    }

    public async Task<List<int>?> GetAccessibleResourceIdsAsync(List<int> roleIds, ResourceType resourceType, Permission required, CancellationToken ct = default)
    {
        var hasWildcard = await db.ResourcePermissions
            .AsNoTracking()
            .AnyAsync(rp =>
                roleIds.Contains(rp.RoleId)
                && rp.ResourceType == resourceType
                && rp.ResourceId == null
                && rp.Permission >= required, ct)
            .ConfigureAwait(false);

        if (hasWildcard) return null;

        return await db.ResourcePermissions
            .AsNoTracking()
            .Where(rp =>
                roleIds.Contains(rp.RoleId)
                && rp.ResourceType == resourceType
                && rp.ResourceId != null
                && rp.Permission >= required)
            .Select(rp => rp.ResourceId!.Value)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<int>> GetOrganizationIdsForUsernameAsync(string username, CancellationToken ct = default)
    {
        return await db.Users
            .AsNoTracking()
            .Where(u => u.Username == username && u.IsActive)
            .SelectMany(u => db.OrganizationMembers.Where(m => m.UserId == u.Id))
            .Select(m => m.OrganizationId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<int>> GetResourceIdsByOrganizationsAsync(ResourceType resourceType, List<int> orgIds, CancellationToken ct = default)
    {
        if (orgIds.Count == 0) return [];
        return resourceType switch
        {
            ResourceType.Server => await db.Servers.AsNoTracking()
                .Where(s => orgIds.Contains(s.OrganizationId))
                .Select(s => s.Id)
                .ToListAsync(ct).ConfigureAwait(false),
            ResourceType.Project => await db.Projects.AsNoTracking()
                .Where(p => orgIds.Contains(p.OrganizationId))
                .Select(p => p.Id)
                .ToListAsync(ct).ConfigureAwait(false),
            ResourceType.PipelineTemplate => await db.PipelineTemplates.AsNoTracking()
                .Where(template => orgIds.Contains(template.OrganizationId))
                .Select(template => template.Id)
                .ToListAsync(ct).ConfigureAwait(false),
            // A pipeline, library or vault is owned by a project, or by an environment or a project
            // server of one (ADR-059 audit, finding 8): the owner's project carries the organization.
            ResourceType.Pipeline => await db.Pipelines.AsNoTracking()
                .Where(item => (item.ProjectId != null && orgIds.Contains(item.Project!.OrganizationId))
                    || (item.ProjectId == null && item.EnvironmentId != null && item.Environment!.ProjectId != null
                        && orgIds.Contains(item.Environment.Project!.OrganizationId))
                    || (item.ProjectId == null && item.EnvironmentId == null && item.ProjectServerId != null
                        && orgIds.Contains(item.ProjectServer!.Project.OrganizationId)))
                .Select(item => item.Id)
                .ToListAsync(ct).ConfigureAwait(false),
            ResourceType.Release => await db.Releases.AsNoTracking()
                .Where(item => orgIds.Contains(item.Project.OrganizationId))
                .Select(item => item.Id)
                .ToListAsync(ct).ConfigureAwait(false),
            ResourceType.Environment => await db.Environments.AsNoTracking()
                .Where(item => item.ProjectId != null && orgIds.Contains(item.Project!.OrganizationId))
                .Select(item => item.Id)
                .ToListAsync(ct).ConfigureAwait(false),
            ResourceType.VariableLibrary => await db.VariableLibraries.AsNoTracking()
                .Where(item => (item.ProjectId != null && orgIds.Contains(item.Project!.OrganizationId))
                    || (item.ProjectId == null && item.EnvironmentId != null && item.Environment!.ProjectId != null
                        && orgIds.Contains(item.Environment.Project!.OrganizationId))
                    || (item.ProjectId == null && item.EnvironmentId == null && item.ProjectServerId != null
                        && orgIds.Contains(item.ProjectServer!.Project.OrganizationId)))
                .Select(item => item.Id)
                .ToListAsync(ct).ConfigureAwait(false),
            ResourceType.Vault => await db.Vaults.AsNoTracking()
                .Where(item => (item.ProjectId != null && orgIds.Contains(item.Project!.OrganizationId))
                    || (item.ProjectId == null && item.EnvironmentId != null && item.Environment!.ProjectId != null
                        && orgIds.Contains(item.Environment.Project!.OrganizationId))
                    || (item.ProjectId == null && item.EnvironmentId == null && item.ProjectServerId != null
                        && orgIds.Contains(item.ProjectServer!.Project.OrganizationId)))
                .Select(item => item.Id)
                .ToListAsync(ct).ConfigureAwait(false),
            ResourceType.ServiceConnection => await db.ServiceConnections.AsNoTracking()
                .Where(item => item.ProjectId != null && orgIds.Contains(item.Project!.OrganizationId))
                .Select(item => item.Id)
                .ToListAsync(ct).ConfigureAwait(false),
            // AgentPool and Docker belong to no organization: only a site-wide grant reaches them.
            _ => []
        };
    }

    public async Task<int?> GetResourceOrganizationIdAsync(ResourceType resourceType, int resourceId, CancellationToken ct = default)
    {
        var query = resourceType switch
        {
            ResourceType.Server => db.Servers.AsNoTracking()
                .Where(s => s.Id == resourceId).Select(s => (int?)s.OrganizationId),
            ResourceType.Project => db.Projects.AsNoTracking()
                .Where(p => p.Id == resourceId).Select(p => (int?)p.OrganizationId),
            ResourceType.PipelineTemplate => db.PipelineTemplates.AsNoTracking()
                .Where(template => template.Id == resourceId).Select(template => (int?)template.OrganizationId),
            // Owned by a project, else by an environment or a project server of one (finding 8).
            ResourceType.Pipeline => db.Pipelines.AsNoTracking()
                .Where(item => item.Id == resourceId)
                .Select(item => item.ProjectId != null ? (int?)item.Project!.OrganizationId
                    : item.EnvironmentId != null ? (item.Environment!.ProjectId != null ? (int?)item.Environment.Project!.OrganizationId : null)
                    : item.ProjectServerId != null ? (int?)item.ProjectServer!.Project.OrganizationId
                    : null),
            ResourceType.Release => db.Releases.AsNoTracking()
                .Where(item => item.Id == resourceId).Select(item => (int?)item.Project.OrganizationId),
            ResourceType.Environment => db.Environments.AsNoTracking()
                .Where(item => item.Id == resourceId && item.ProjectId != null).Select(item => (int?)item.Project!.OrganizationId),
            ResourceType.VariableLibrary => db.VariableLibraries.AsNoTracking()
                .Where(item => item.Id == resourceId)
                .Select(item => item.ProjectId != null ? (int?)item.Project!.OrganizationId
                    : item.EnvironmentId != null ? (item.Environment!.ProjectId != null ? (int?)item.Environment.Project!.OrganizationId : null)
                    : item.ProjectServerId != null ? (int?)item.ProjectServer!.Project.OrganizationId
                    : null),
            ResourceType.Vault => db.Vaults.AsNoTracking()
                .Where(item => item.Id == resourceId)
                .Select(item => item.ProjectId != null ? (int?)item.Project!.OrganizationId
                    : item.EnvironmentId != null ? (item.Environment!.ProjectId != null ? (int?)item.Environment.Project!.OrganizationId : null)
                    : item.ProjectServerId != null ? (int?)item.ProjectServer!.Project.OrganizationId
                    : null),
            ResourceType.ServiceConnection => db.ServiceConnections.AsNoTracking()
                .Where(item => item.Id == resourceId && item.ProjectId != null).Select(item => (int?)item.Project!.OrganizationId),
            _ => null
        };
        return query is null ? null : await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<int?> GetProjectOrganizationIdAsync(int projectId, CancellationToken ct = default)
    {
        return await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => (int?)p.OrganizationId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<OwnerProject?> GetEnvironmentProjectAsync(int environmentId, CancellationToken ct = default)
    {
        return await db.Environments.AsNoTracking()
            .Where(environment => environment.Id == environmentId)
            .Select(environment => new OwnerProject(environment.ProjectId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<OwnerProject?> GetProjectServerProjectAsync(int projectServerId, CancellationToken ct = default)
    {
        return await db.ProjectServers.AsNoTracking()
            .Where(projectServer => projectServer.Id == projectServerId)
            .Select(projectServer => new OwnerProject(projectServer.ProjectId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<string>> GetUsernamesWithPermissionAsync(
        IReadOnlyCollection<string> usernames, ResourceType resourceType, int resourceId, int? organizationId,
        Permission required, CancellationToken ct = default)
    {
        if (usernames.Count == 0) return [];
        // One query for the whole list, the same two paths HasPermissionAsync(username) walks one user
        // at a time: the site-wide Admin role, or a role held for the owning organization while still a
        // member of it. Both are bounded by the role's permission on the type (generic or this id).
        var granting = db.ResourcePermissions
            .Where(rp => rp.ResourceType == resourceType
                && (rp.ResourceId == null || rp.ResourceId == resourceId)
                && rp.Permission >= required)
            .Select(rp => rp.RoleId);
        return await db.Users
            .AsNoTracking()
            .Where(user => usernames.Contains(user.Username) && user.IsActive)
            .Where(user =>
                user.UserRoles.Any(site => site.Role.Name == RoleNames.Admin && granting.Contains(site.RoleId))
                || (organizationId != null && db.UserOrganizationRoles.Any(assignment =>
                    assignment.UserId == user.Id
                    && assignment.OrganizationId == organizationId
                    && granting.Contains(assignment.RoleId)
                    && db.OrganizationMembers.Any(member =>
                        member.UserId == user.Id && member.OrganizationId == organizationId))))
            .Select(user => user.Username)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
