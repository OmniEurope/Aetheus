// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.VariableLibraries.Events;
using Aetheus.Back.Data;

namespace Aetheus.Back.Components.PortAllocation;

public class PortAllocationRepository(AppDbContext db) : IPortAllocationRepository
{
    public async Task<LibraryScope?> FindLibraryScopeAsync(int libraryId, CancellationToken ct = default)
    {
        var library = await db.VariableLibraries
            .AsNoTracking()
            .Where(candidate => candidate.Id == libraryId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Name,
                candidate.ProjectId,
                candidate.EnvironmentId,
                candidate.ProjectServerId,
                // The owning project of each scope, resolved here so the service never has to guess it.
                EnvironmentProjectId = candidate.Environment != null ? candidate.Environment.ProjectId : null,
                ProjectServerProjectId = candidate.ProjectServer != null
                    ? (int?)candidate.ProjectServer.ProjectId
                    : null,
                ProjectServerServerId = candidate.ProjectServer != null
                    ? candidate.ProjectServer.ServerId
                    : null
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (library is null) return null;

        return new LibraryScope(
            library.Id,
            library.Name,
            library.ProjectId ?? library.EnvironmentProjectId ?? library.ProjectServerProjectId,
            library.EnvironmentId,
            library.ProjectServerId,
            library.ProjectServerServerId);
    }

    public async Task<List<(int ServerId, string ServerName)>> GetCandidateServersAsync(
        LibraryScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // A project-server library names its host outright; anything else offers the servers its scope
        // reaches, and the operator chooses. A server attached to an environment of the project is a
        // server of the project: the operator never attaches it twice (R-83).
        IQueryable<int> serverIds;
        if (scope.ProjectServerId is not null)
        {
            serverIds = db.ProjectServers
                .Where(projectServer => projectServer.Id == scope.ProjectServerId && projectServer.ServerId != null)
                .Select(projectServer => projectServer.ServerId!.Value);
        }
        else if (scope.EnvironmentId is not null)
        {
            serverIds = db.EnvironmentServers
                .Where(link => link.EnvironmentId == scope.EnvironmentId)
                .Select(link => link.ServerId);
        }
        else if (scope.ProjectId is { } projectId)
        {
            serverIds = db.ProjectServers
                .Where(projectServer => projectServer.ProjectId == projectId && projectServer.ServerId != null)
                .Select(projectServer => projectServer.ServerId!.Value)
                .Union(db.EnvironmentServers
                    .Where(link => link.Environment.ProjectId == projectId)
                    .Select(link => link.ServerId));
        }
        else
        {
            return [];
        }

        var servers = await db.Servers
            .AsNoTracking()
            .Where(server => serverIds.Contains(server.Id))
            .OrderBy(server => server.Name)
            .Select(server => new { server.Id, server.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. servers.Select(server => (server.Id, server.Name))];
    }

    public async Task<List<int>> GetDeclaredPortEntriesAsync(int libraryId, CancellationToken ct = default)
    {
        var values = await db.VariableLibraryEntries
            .AsNoTracking()
            .Where(entry => entry.VariableLibraryId == libraryId
                && entry.Key.StartsWith(PortRegistryLimits.PortKeyPrefix))
            .Select(entry => entry.Value)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // A PORT_* entry holding something that is not a port is left out rather than reported as 0:
        // the check would then answer about a port nobody asked about.
        return
        [
            .. values
                .Select(value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                    ? port
                    : 0)
                .Where(port => port is > 0 and <= 65535)
                .Distinct()
                .Order()
        ];
    }

    public async Task<List<ExistingLibraryEntry>> FindEntriesByKeyAsync(
        int libraryId, IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        return await db.VariableLibraryEntries
            .AsNoTracking()
            .Where(entry => entry.VariableLibraryId == libraryId && keys.Contains(entry.Key))
            .Select(entry => new ExistingLibraryEntry(entry.Id, entry.Key, entry.Value))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> ServerExistsAsync(int serverId, CancellationToken ct = default) =>
        await db.Servers.AnyAsync(server => server.Id == serverId, ct).ConfigureAwait(false);

    public async Task<LibraryScope> ResolveScopeAsync(
        int libraryId, string libraryName, LibraryPortScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var projectId = scope.ProjectId;
        if (projectId is null && scope.EnvironmentId is { } environmentId)
        {
            projectId = await db.Environments.AsNoTracking()
                .Where(environment => environment.Id == environmentId)
                .Select(environment => environment.ProjectId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        int? hostId = null;
        if (scope.ProjectServerId is { } projectServerId)
        {
            var projectServer = await db.ProjectServers.AsNoTracking()
                .Where(candidate => candidate.Id == projectServerId)
                .Select(candidate => new { candidate.ProjectId, candidate.ServerId })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            projectId ??= projectServer?.ProjectId;
            hostId = projectServer?.ServerId;
        }

        return new LibraryScope(
            libraryId, libraryName, projectId, scope.EnvironmentId, scope.ProjectServerId, hostId);
    }

    public async Task<bool> IsPortHeldByProjectEntryAsync(int projectId, int port, CancellationToken ct = default)
    {
        var value = port.ToString(CultureInfo.InvariantCulture);
        return await db.VariableLibraryEntries.AsNoTracking()
            .AnyAsync(entry => entry.Key.StartsWith(PortRegistryLimits.PortKeyPrefix)
                && entry.Value.Trim() == value
                && (entry.VariableLibrary.ProjectId == projectId
                    || entry.VariableLibrary.Environment!.ProjectId == projectId
                    || entry.VariableLibrary.ProjectServer!.ProjectId == projectId), ct)
            .ConfigureAwait(false);
    }

    public async Task<List<LibraryPortSource>> GetPortLibrariesAsync(int? projectId = null, CancellationToken ct = default)
    {
        var rows = await db.VariableLibraryEntries.AsNoTracking()
            .Where(entry => entry.Key.StartsWith(PortRegistryLimits.PortKeyPrefix))
            .Where(entry => projectId == null
                || entry.VariableLibrary.ProjectId == projectId
                || entry.VariableLibrary.Environment!.ProjectId == projectId
                || entry.VariableLibrary.ProjectServer!.ProjectId == projectId)
            .Select(entry => new
            {
                entry.VariableLibraryId,
                LibraryName = entry.VariableLibrary.Name,
                entry.VariableLibrary.ProjectId,
                entry.VariableLibrary.EnvironmentId,
                entry.VariableLibrary.ProjectServerId,
                entry.Key,
                entry.Value
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return
        [
            .. rows.GroupBy(row => row.VariableLibraryId)
                .Select(group => new LibraryPortSource(
                    group.Key,
                    group.First().LibraryName,
                    new LibraryPortScope(group.First().ProjectId, group.First().EnvironmentId, group.First().ProjectServerId),
                    [.. group.Select(row => LibraryPortEntry.TryRead(row.Key, row.Value, out var port) ? port : null)
                        .OfType<LibraryPortEntry>()]))
                .Where(source => source.Entries.Count > 0)
                .OrderBy(source => source.LibraryId)
        ];
    }
}
