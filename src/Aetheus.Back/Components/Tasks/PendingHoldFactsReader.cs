// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Tasks;

/// <summary>Reads, for a server whose queued task was put on hold, the facts <see cref="PendingHoldDiagnostics"/>
/// explains. Two small read-only queries, run once per task entering its hold (never per sweep).</summary>
internal static class PendingHoldFactsReader
{
    private const int MaximumActiveTasks = 20;

    internal static async Task<PendingHoldServerFacts?> ReadAsync(AppDbContext db, int serverId, CancellationToken ct)
    {
        // A retired server is read too: its pending work is swept like any other, and the hold must say why.
        var server = await db.Servers.IgnoreQueryFilters([ServerQueryFilters.ExcludeRetired])
            .AsNoTracking()
            .Where(candidate => candidate.Id == serverId)
            .Select(candidate => new
            {
                candidate.Name,
                candidate.Status,
                candidate.DeletedAt,
                candidate.LastHeartbeat,
                candidate.AgentSessionLeaseExpiresAt,
                candidate.AgentSessionId,
                candidate.AgentVersion,
                candidate.AgentProtocolVersion,
                candidate.AgentCapabilitiesJson,
                candidate.AgentUpdateReserved,
                candidate.AgentUpdateReservedAt
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (server is null) return null;

        var active = await db.Tasks.AsNoTracking()
            .Where(task => task.ServerId == serverId
                && (task.Status == TaskExecutionStatus.Assigned || task.Status == TaskExecutionStatus.Running))
            .OrderBy(task => task.AssignedAt ?? task.CreatedAt)
            .Take(MaximumActiveTasks)
            .Select(task => new { task.Id, task.Status, task.Operation, task.StartedAt, task.AssignedAt, task.CreatedAt })
            .ToListAsync(ct).ConfigureAwait(false);
        var pending = await db.Tasks.AsNoTracking()
            .CountAsync(task => task.ServerId == serverId && task.Status == TaskExecutionStatus.Pending, ct)
            .ConfigureAwait(false);

        return new PendingHoldServerFacts(
            serverId,
            server.Name,
            server.Status,
            server.DeletedAt,
            server.LastHeartbeat,
            server.AgentSessionLeaseExpiresAt,
            server.AgentSessionId,
            server.AgentVersion,
            server.AgentProtocolVersion,
            server.AgentCapabilitiesJson,
            server.AgentUpdateReserved,
            server.AgentUpdateReservedAt,
            [.. active.Select(task => new PendingHoldActiveTask(
                task.Id, task.Status, task.Operation, task.StartedAt ?? task.AssignedAt ?? task.CreatedAt))],
            pending);
    }
}
