// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Components.VariableLibraries;

namespace Aetheus.Back.Components.PortAllocation;

/// <summary>Reserves free ports and writes their <c>PORT_*</c> library entries in one transaction,
/// preventing reservations without usable configuration.</summary>
public sealed class PortAllocationService(
    IPortAllocationRepository repo,
    IPortRegistryService registry,
    IVariableLibraryService libraries,
    IDbTransactionScope transaction,
    ILogger<PortAllocationService> logger) : IPortAllocationService
{
    public async Task<PortAllocationTargetsDto> GetTargetsAsync(int libraryId, CancellationToken ct = default)
    {
        var scope = await repo.FindLibraryScopeAsync(libraryId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Variable library {libraryId} was not found.");

        var servers = await repo.GetCandidateServersAsync(scope, ct).ConfigureAwait(false);
        var targets = servers
            .Select(server => new PortAllocationTargetDto { ServerId = server.ServerId, ServerName = server.ServerName })
            .ToList();

        return new PortAllocationTargetsDto
        {
            Servers = targets,
            // Pre-selected whenever exactly one server is reachable (R-88): a lone choice offered as a
            // selection nobody can make leaves the button disabled. Two servers, and the operator asks.
            PreselectedServerId = scope.ProjectId is not null && targets.Count == 1
                    ? targets[0].ServerId
                    : null,
            ProjectId = scope.ProjectId,
            Blocker = Blocker(scope, targets)
        };
    }

    public async Task<List<VariableEntryDto>> AllocateIntoLibraryAsync(
        int libraryId, int serverId, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var scope = await repo.FindLibraryScopeAsync(libraryId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Variable library {libraryId} was not found.");
        if (!await repo.ServerExistsAsync(serverId, ct).ConfigureAwait(false))
            throw new NotFoundException($"Server {serverId} was not found.");

        var normalized = NormalizeKeys(keys);
        if (scope.ProjectId is not { } projectId)
            throw new BadRequestException(
                $"'{scope.LibraryName}' belongs to no project, so a reservation could not name a holder "
                + "any deployment would recognise. Attach the library to a project first.");

        // A key the library already holds is filled when it is empty (R-85: PORT_FRONT and PORT_BACK exist
        // empty) and refused when it carries a value, before any port is reserved: a value somebody typed
        // is not overwritten behind their back.
        var existing = await repo.FindEntriesByKeyAsync(libraryId, normalized, ct).ConfigureAwait(false);
        var holding = existing.Where(entry => !string.IsNullOrWhiteSpace(entry.Value)).Select(entry => entry.Key).ToList();
        if (holding.Count > 0)
            throw new BadRequestException(
                $"{string.Join(", ", holding.Select(key => $"'{key}'"))} already holds a value in "
                + $"'{scope.LibraryName}'; empty it first, or name another key.");
        var existingIds = existing.ToDictionary(entry => entry.Key, entry => entry.Id, StringComparer.Ordinal);

        // One transaction, now that the scope is re-entrant: the library service opens its own inside
        // this one and joins it instead of making EF refuse a nested begin. The compensation below
        // stays as the belt for the provider that has no transactions at all.
        return transaction.IsRelational
            ? await transaction
                .ExecuteInTransactionAsync(
                    () => WriteAsync(libraryId, serverId, projectId, scope, normalized, existingIds, ct), ct)
                .ConfigureAwait(false)
            : await WriteAsync(libraryId, serverId, projectId, scope, normalized, existingIds, ct)
                .ConfigureAwait(false);
    }

    public async Task<int?> GetLibraryProjectIdAsync(int libraryId, CancellationToken ct = default)
        => (await repo.FindLibraryScopeAsync(libraryId, ct).ConfigureAwait(false))?.ProjectId;

    public async Task<List<int>> GetReleaseServerIdsAsync(int libraryId, CancellationToken ct = default)
    {
        var scope = await repo.FindLibraryScopeAsync(libraryId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Variable library {libraryId} was not found.");
        if (scope.ProjectId is null) return [];
        var servers = await repo.GetCandidateServersAsync(scope, ct).ConfigureAwait(false);
        return servers.Select(server => server.ServerId).ToList();
    }

    public async Task<PortCheckResultDto> CheckLibraryPortsAsync(
        int libraryId, int serverId, CancellationToken ct = default)
    {
        var scope = await repo.FindLibraryScopeAsync(libraryId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Variable library {libraryId} was not found.");

        var ports = await repo.GetDeclaredPortEntriesAsync(libraryId, ct).ConfigureAwait(false);
        if (ports.Count == 0)
            throw new BadRequestException(
                $"This library holds no '{PortRegistryLimits.PortKeyPrefix}' entry to check.");

        // Availability is evaluated for the library's project so its own listening ports are not
        // treated as conflicts.
        return await registry.CheckPortsAsync(serverId, ports, scope.ProjectId, ct).ConfigureAwait(false);
    }

    public async Task<int> ReleaseLibraryPortAsync(int libraryId, int port, CancellationToken ct = default)
    {
        var scope = await repo.FindLibraryScopeAsync(libraryId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Variable library {libraryId} was not found.");
        if (scope.ProjectId is not { } projectId) return 0;
        if (port is <= 0 or > 65535) return 0;

        var ownerKey = PortRegistryService.ProjectOwnerKey(projectId);
        var servers = await repo.GetCandidateServersAsync(scope, ct).ConfigureAwait(false);

        var released = 0;
        foreach (var server in servers)
        {
            if (await registry.ReleaseOwnedAsync(server.ServerId, port, ownerKey, ct).ConfigureAwait(false))
                released++;
        }

        if (released > 0)
            logger.LogInformation(
                "Released port {Port} held by {Owner} on {Count} server(s) after its library entry was deleted.",
                port, ownerKey, released);
        return released;
    }

    /// <summary>
    /// Reserves, then writes the entries. Wrapped in one transaction by the caller on a relational
    /// provider; the compensation here is what covers the InMemory provider, which has no transactions,
    /// and the case where the rollback itself cannot run. A compensation that fails is logged as an
    /// error naming the ports, the one case a human has to clean up on the registry page.
    /// </summary>
    private async Task<List<VariableEntryDto>> WriteAsync(
        int libraryId, int serverId, int projectId, LibraryScope scope,
        IReadOnlyList<string> keys, IReadOnlyDictionary<string, int> existingIds, CancellationToken ct)
    {
        var ownerKey = PortRegistryService.ProjectOwnerKey(projectId);
        var ports = await registry
            .AllocateAsync(serverId, keys.Count, ownerKey, scope.LibraryName, projectId, ct)
            .ConfigureAwait(false);

        var created = new List<VariableEntryDto>(keys.Count);
        try
        {
            for (var index = 0; index < keys.Count; index++)
            {
                var key = keys[index];
                var value = ports[index].ToString(CultureInfo.InvariantCulture);
                created.Add(existingIds.TryGetValue(key, out var entryId)
                    ? await libraries
                          .UpdateEntryAsync(
                              libraryId, entryId, new UpdateVariableEntryRequest { Key = key, Value = value }, ct)
                          .ConfigureAwait(false)
                      ?? throw new NotFoundException($"Entry '{key}' of library {libraryId} was not found.")
                    : await libraries
                        .CreateEntryAsync(libraryId, new CreateVariableEntryRequest { Key = key, Value = value }, ct)
                        .ConfigureAwait(false));
            }
        }
        catch (Exception ex)
        {
            await CompensateAsync(serverId, ports, ownerKey, ex, ct).ConfigureAwait(false);
            throw;
        }

        logger.LogInformation(
            "Allocated ports {Ports} on server {ServerId} into library {LibraryId}.",
            string.Join(", ", ports), serverId, libraryId);
        return created;
    }

    /// <summary>Releases what the failed allocation had already reserved, and says so if it cannot.</summary>
    private async Task CompensateAsync(
        int serverId, IReadOnlyList<int> ports, string ownerKey, Exception cause, CancellationToken ct)
    {
        try
        {
            foreach (var port in ports)
                await registry.ReleaseOwnedAsync(serverId, port, ownerKey, ct).ConfigureAwait(false);

            logger.LogWarning(
                cause,
                "Writing the port variables failed; released the ports {Ports} reserved on server {ServerId}.",
                string.Join(", ", ports), serverId);
        }
        catch (Exception releaseFailure)
        {
            logger.LogError(
                releaseFailure,
                "Writing the port variables failed AND the ports {Ports} reserved on server {ServerId} for "
                + "'{Owner}' could not be released; they stay blocked until somebody releases them on the "
                + "server's Ports page.",
                string.Join(", ", ports), serverId, ownerKey);
        }
    }

    /// <summary>
    /// Why this library cannot allocate. A code and not a sentence: the UI is localized, and a
    /// server-composed English message would land verbatim in a French screen.
    /// </summary>
    private static PortAllocationBlocker Blocker(LibraryScope scope, List<PortAllocationTargetDto> targets)
    {
        if (scope.ProjectId is null) return PortAllocationBlocker.NoProject;
        return targets.Count == 0 ? PortAllocationBlocker.NoServer : PortAllocationBlocker.None;
    }

    /// <summary>
    /// Keys must carry the <c>PORT_</c> prefix because it is the only thing the preflight guard reads.
    /// A key without it would produce a variable that looks protected and is not, so it is refused
    /// rather than silently renamed.
    /// </summary>
    private static List<string> NormalizeKeys(IReadOnlyList<string> keys)
    {
        var normalized = new List<string>(keys.Count);
        foreach (var key in keys)
        {
            var trimmed = (key ?? string.Empty).Trim();
            if (trimmed.Length == 0) continue;
            if (!trimmed.StartsWith(PortRegistryLimits.PortKeyPrefix, StringComparison.Ordinal))
                throw new BadRequestException(
                    $"'{trimmed}' must start with '{PortRegistryLimits.PortKeyPrefix}': the pipeline "
                    + "preflight guard only reads keys carrying that prefix.");
            if (normalized.Contains(trimmed, StringComparer.Ordinal))
                throw new BadRequestException($"'{trimmed}' is asked for twice.");
            normalized.Add(trimmed);
        }

        if (normalized.Count == 0)
            throw new BadRequestException("Name at least one port variable to create.");
        if (normalized.Count > PortRegistryLimits.MaxPortsPerAllocation)
            throw new BadRequestException(
                $"At most {PortRegistryLimits.MaxPortsPerAllocation} port variables at a time.");
        return normalized;
    }
}
