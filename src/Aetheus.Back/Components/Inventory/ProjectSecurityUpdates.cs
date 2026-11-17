// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Inventory;

/// <summary>What a server last reported of its operating-system patches. An unknown state is never a zero.</summary>
internal enum ServerUpdatesState
{
    /// <summary>The server never reported a state: nothing is known.</summary>
    NotReported,

    /// <summary>The last probe failed (or the server runs no apt): the counts mean nothing.</summary>
    Unknown,

    UpToDate,
    Pending
}

internal sealed record ServerSecurityUpdates(
    int ServerId, string ServerName, ServerUpdatesState State, int PendingSecurity, int PendingTotal, DateTime? CheckedAt);

/// <summary>The patch state of every server of one project. Only the servers whose state is known add to the counts.</summary>
internal sealed record ProjectSecurityUpdates(int ProjectId, IReadOnlyList<ServerSecurityUpdates> Servers)
{
    public int PendingSecurity => Servers.Where(server => server.State == ServerUpdatesState.Pending).Sum(server => server.PendingSecurity);

    public int UnknownServers => Servers.Count(server => server.State is ServerUpdatesState.NotReported or ServerUpdatesState.Unknown);
}

internal interface IProjectSecurityUpdatesRepository
{
    Task<ProjectSecurityUpdates> GetAsync(int projectId, CancellationToken ct);
}

/// <summary>
/// The patch state of the servers of a project, as each server last reported it (ADR-024), through <c>ProjectServer</c>.
/// The state of a host concerns every project it hosts, so a shared server counts for each of them (the images it lists
/// do not: see <see cref="ServerInventoryMapper"/>). The reports carry counts and package names, never a CVE identifier,
/// so they are shown as they are and never matched against advisories.
/// </summary>
internal sealed class ProjectSecurityUpdatesRepository(AppDbContext db) : IProjectSecurityUpdatesRepository
{
    public async Task<ProjectSecurityUpdates> GetAsync(int projectId, CancellationToken ct)
    {
        var serverIds = await db.ProjectServers.AsNoTracking()
            .Where(link => link.ProjectId == projectId && link.ServerId != null)
            .Select(link => link.ServerId!.Value)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        var rows = await db.Servers.AsNoTracking()
            .Where(server => serverIds.Contains(server.Id))
            .OrderBy(server => server.Id)
            .Select(server => new { server.Id, server.Name, Updates = server.SecurityUpdatesState })
            .ToListAsync(ct).ConfigureAwait(false);
        return new ProjectSecurityUpdates(projectId, [.. rows.Select(row => ToState(row.Id, row.Name, row.Updates))]);
    }

    /// <summary>A probe that failed reads as unknown, whatever its counts say: they are zero because nothing was read.</summary>
    private static ServerSecurityUpdates ToState(int id, string name, SecurityUpdatesState? updates)
    {
        if (updates is null) return new ServerSecurityUpdates(id, name, ServerUpdatesState.NotReported, 0, 0, null);
        if (!updates.Probed) return new ServerSecurityUpdates(id, name, ServerUpdatesState.Unknown, 0, 0, updates.CheckedAt);
        var state = updates.PendingSecurity > 0 || updates.PendingTotal > 0 ? ServerUpdatesState.Pending : ServerUpdatesState.UpToDate;
        return new ServerSecurityUpdates(id, name, state, updates.PendingSecurity, updates.PendingTotal, updates.CheckedAt);
    }
}
