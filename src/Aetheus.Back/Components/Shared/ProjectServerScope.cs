// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;

namespace Aetheus.Back.Components.Shared;

/// <summary>
/// Which servers belong to a project and which projects a server belongs to (recette R-83). A server attached to
/// an environment of a project is a server of that project: the user attaches it once, to the environment, and
/// never a second time to the project. Both directions are the union of the project's own server links
/// (<c>ProjectServers</c>) and the servers of the environments that belong to it (<c>EnvironmentServers</c> joined to
/// <c>Environments.ProjectId</c>), so an environment of another project, or with no project, never adds a server.
///
/// These are readers only. Whatever writes or deletes a <c>ProjectServer</c> row keeps working on
/// <c>ProjectServers</c> alone: an environment server has no such row to change.
/// </summary>
internal static class ProjectServerScope
{
    /// <summary>The ids of the servers the project reaches, each once.</summary>
    internal static IQueryable<int> ServerIdsOfProject(this AppDbContext db, int projectId)
    {
        ArgumentNullException.ThrowIfNull(db);
        // A server of another organization is never one of the project's, even when a row links it (written
        // before the organization checks, or copied): an AI task or a deployment would run there (RBAC audit,
        // finding 2).
        var linked = db.ProjectServers
            .Where(projectServer => projectServer.ProjectId == projectId && projectServer.ServerId != null
                && projectServer.Server!.OrganizationId == projectServer.Project.OrganizationId)
            .Select(projectServer => projectServer.ServerId!.Value);
        var viaEnvironment = db.EnvironmentServers
            .Where(link => link.Environment.ProjectId == projectId
                && link.Server.OrganizationId == link.Environment.Project!.OrganizationId)
            .Select(link => link.ServerId);
        return linked.Union(viaEnvironment);
    }

    /// <summary>The ids of the projects the server is attached to, directly or through an environment, each
    /// once.</summary>
    internal static IQueryable<int> ProjectIdsOfServer(this AppDbContext db, int serverId)
    {
        ArgumentNullException.ThrowIfNull(db);
        // The same organization rule in this direction.
        var linked = db.ProjectServers
            .Where(projectServer => projectServer.ServerId == serverId
                && projectServer.Server!.OrganizationId == projectServer.Project.OrganizationId)
            .Select(projectServer => projectServer.ProjectId);
        var viaEnvironment = db.EnvironmentServers
            .Where(link => link.ServerId == serverId && link.Environment.ProjectId != null
                && link.Server.OrganizationId == link.Environment.Project!.OrganizationId)
            .Select(link => link.Environment.ProjectId!.Value);
        return linked.Union(viaEnvironment);
    }
}
