// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Projects;

/// <summary>
/// The query behind a project's server list (recette R-83): the project's own server links, followed by the servers
/// attached to one of its environments that the project is not already linked to. A server in several environments
/// of the project is listed once, under the environment with the lowest id; a server linked to the project and to
/// its environment is listed once, as the project's link. Only environments whose <c>ProjectId</c> is the project
/// count: another project's environment never adds a server here.
/// </summary>
internal static class ProjectServerRows
{
    internal static IQueryable<ProjectServerRow> ForProject(AppDbContext db, int projectId)
    {
        var linked = db.ProjectServers
            .Where(projectServer => projectServer.ProjectId == projectId)
            .Select(projectServer => new ProjectServerRow
            {
                Id = projectServer.Id,
                ProjectId = projectServer.ProjectId,
                ServerId = projectServer.ServerId,
                Type = projectServer.Type,
                DisplayName = projectServer.DisplayName,
                Host = projectServer.Host,
                Port = projectServer.Port,
                Notes = projectServer.Notes,
                CreatedAt = projectServer.CreatedAt,
                UpdatedAt = projectServer.UpdatedAt,
                ServerName = projectServer.Server != null ? projectServer.Server.Name : null,
                ServerHostname = projectServer.Server != null ? projectServer.Server.Hostname : null,
                ServerStatus = projectServer.Server != null ? (ServerStatus?)projectServer.Server.Status : null,
                // Both operands of the Concat set every member, so their shapes are the same.
                ViaEnvironmentId = null,
                ViaEnvironmentName = null
            });

        var firstEnvironmentOfServer = db.EnvironmentServers
            .Where(link => link.Environment.ProjectId == projectId)
            .GroupBy(link => link.ServerId)
            .Select(group => new { ServerId = group.Key, EnvironmentId = group.Min(link => link.EnvironmentId) });

        var viaEnvironment = firstEnvironmentOfServer
            .Join(db.Servers, first => first.ServerId, server => server.Id,
                (first, server) => new { first.EnvironmentId, Server = server })
            .Where(entry => !db.ProjectServers.Any(projectServer =>
                projectServer.ProjectId == projectId && projectServer.ServerId == entry.Server.Id))
            .Join(db.Environments, entry => entry.EnvironmentId, environment => environment.Id,
                (entry, environment) => new ProjectServerRow
                {
                    Id = 0,
                    ProjectId = projectId,
                    ServerId = entry.Server.Id,
                    Type = ProjectServerType.AgentServer,
                    DisplayName = entry.Server.Name,
                    Host = entry.Server.Hostname,
                    Port = null,
                    Notes = null,
                    CreatedAt = entry.Server.CreatedAt,
                    UpdatedAt = entry.Server.UpdatedAt,
                    ServerName = entry.Server.Name,
                    ServerHostname = entry.Server.Hostname,
                    ServerStatus = entry.Server.Status,
                    ViaEnvironmentId = environment.Id,
                    ViaEnvironmentName = environment.Name
                });

        return linked.Concat(viaEnvironment);
    }
}
