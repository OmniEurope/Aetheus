// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Projects;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Recette R-77: the servers the production setup may offer a project that has none yet, and the attachment of the
/// one chosen, so a new project's deployment, ports and run dialog all find it without a manual step.</summary>
public interface IPipelineSetupServerRepository
{
    /// <summary>The servers among <paramref name="administeredIds"/> (every server when null: the caller administers them all),
    /// by name.</summary>
    Task<List<(int Id, string Name)>> ListServersAsync(IReadOnlyCollection<int>? administeredIds, CancellationToken ct = default);

    /// <summary>Attaches the agent server to the project, once: an attachment already there is kept. Returns whether a new
    /// one was written.</summary>
    Task<bool> AttachAgentServerAsync(int projectId, int serverId, CancellationToken ct = default);
}

public sealed class PipelineSetupServerRepository(AppDbContext db, TimeProvider timeProvider) : IPipelineSetupServerRepository
{
    public async Task<List<(int Id, string Name)>> ListServersAsync(
        IReadOnlyCollection<int>? administeredIds, CancellationToken ct = default)
    {
        var query = db.Servers.AsNoTracking();
        if (administeredIds is not null) query = query.Where(server => administeredIds.Contains(server.Id));
        var rows = await query
            .OrderBy(server => server.Name)
            .Select(server => new { server.Id, server.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return [.. rows.Select(row => (row.Id, row.Name))];
    }

    public async Task<bool> AttachAgentServerAsync(int projectId, int serverId, CancellationToken ct = default)
    {
        if (await db.ProjectServers.AnyAsync(link => link.ProjectId == projectId && link.ServerId == serverId, ct)
                .ConfigureAwait(false))
            return false;
        var server = await db.Servers.AsNoTracking()
            .Where(item => item.Id == serverId)
            .Select(item => new { item.Name, item.Hostname })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new Aetheus.Back.Exceptions.NotFoundException($"Server {serverId} not found.");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.ProjectServers.Add(new ProjectServer
        {
            ProjectId = projectId,
            ServerId = serverId,
            Type = ProjectServerType.AgentServer,
            DisplayName = server.Name,
            Host = string.IsNullOrWhiteSpace(server.Hostname) ? server.Name : server.Hostname,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
