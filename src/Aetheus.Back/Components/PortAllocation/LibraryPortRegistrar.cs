// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Components.VariableLibraries.Events;

namespace Aetheus.Back.Components.PortAllocation;

/// <summary>
/// Keeps the port registry complete: every <c>PORT_*</c> entry written in a library owned by a project is
/// a declared reservation for that project (owner key <see cref="PortRegistryService.ProjectOwnerKey"/>) on
/// the servers the library reaches. Without it, a port written in a library but never declared looked free
/// to <see cref="IPortRegistryService.AllocateAsync"/>, which handed it to another project: the scheduled
/// nightly then found its ports registered to somebody else and was refused every night.
///
/// A library that belongs to no project names no holder and claims nothing. A project-level library that
/// reaches several servers claims nothing either: which of them it deploys to is not known, and a claim on
/// a host it never uses would refuse its writes over somebody else's legitimate port.
/// </summary>
internal sealed class LibraryPortRegistrar(
    IPortAllocationRepository repo,
    IPortRegistryService registry,
    ILogger<LibraryPortRegistrar> logger)
{
    private readonly record struct ClaimKey(int ProjectId, int ServerId, int Port);

    private sealed record ClaimDetail(string ServerName, string EntryKey);

    /// <summary>Applies one change strictly: a port another owner holds throws a <see cref="ConflictException"/>
    /// naming the holder, the entry and the server, so the write that announced it is refused.</summary>
    public async Task ApplyAsync(VariableLibraryPortsChangedEvent change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);

        var before = await ClaimsAsync(change.LibraryId, change.LibraryName, change.OldScope, change.OldEntries, ct)
            .ConfigureAwait(false);
        var after = await ClaimsAsync(change.LibraryId, change.LibraryName, change.NewScope, change.NewEntries, ct)
            .ConfigureAwait(false);

        foreach (var gone in before.Keys.Where(claim => !after.ContainsKey(claim)))
        {
            // Another PORT_* entry of the project may still stand behind the same port.
            if (await repo.IsPortHeldByProjectEntryAsync(gone.ProjectId, gone.Port, ct).ConfigureAwait(false)) continue;
            await registry.ReleaseOwnedAsync(
                gone.ServerId, gone.Port, PortRegistryService.ProjectOwnerKey(gone.ProjectId), ct).ConfigureAwait(false);
        }

        foreach (var (claim, detail) in after)
            await ClaimAsync(claim, detail, change.LibraryName, ct).ConfigureAwait(false);
    }

    /// <summary>Carries one existing library into the registry. A contested port is reported and left with its
    /// holder, as the other backfills do: overwriting it at startup would steal a port from a running deployment.</summary>
    public async Task<(int Carried, int Contested)> BackfillAsync(LibraryPortSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        var claims = await ClaimsAsync(source.LibraryId, source.LibraryName, source.Scope, source.Entries, ct)
            .ConfigureAwait(false);
        var carried = 0;
        var contested = 0;
        foreach (var (claim, detail) in claims)
        {
            try
            {
                await ClaimAsync(claim, detail, source.LibraryName, ct).ConfigureAwait(false);
                carried++;
            }
            catch (ConflictException ex)
            {
                contested++;
                logger.LogWarning(
                    "Port registry backfill left {EntryKey}={Port} of library '{Library}' alone: {Reason}",
                    detail.EntryKey, claim.Port, source.LibraryName, ex.Message);
            }
        }

        return (carried, contested);
    }

    private async Task ClaimAsync(ClaimKey claim, ClaimDetail detail, string libraryName, CancellationToken ct)
    {
        try
        {
            var listener = await registry.ClaimPortAsync(
                claim.ServerId, claim.Port, PortRegistryService.ProjectOwnerKey(claim.ProjectId), libraryName,
                claim.ProjectId, ct).ConfigureAwait(false);
            if (listener is not null)
                logger.LogWarning(
                    "{EntryKey}={Port} of library '{Library}' was registered, but '{Listener}' was seen listening on that port of server '{Server}'.",
                    detail.EntryKey, claim.Port, libraryName, listener, detail.ServerName);
        }
        catch (ConflictException ex)
        {
            throw new ConflictException(
                $"{detail.EntryKey} = {claim.Port} cannot be registered on server '{detail.ServerName}': {ex.Message}");
        }
    }

    /// <summary>The (project, server, port) triples the entries claim in this scope, empty when the scope names
    /// no holder or no server.</summary>
    private async Task<Dictionary<ClaimKey, ClaimDetail>> ClaimsAsync(
        int libraryId, string libraryName, LibraryPortScope scope, IReadOnlyList<LibraryPortEntry> entries,
        CancellationToken ct)
    {
        var claims = new Dictionary<ClaimKey, ClaimDetail>();
        if (entries.Count == 0) return claims;

        var resolved = await repo.ResolveScopeAsync(libraryId, libraryName, scope, ct).ConfigureAwait(false);
        if (resolved.ProjectId is not { } projectId) return claims;

        var servers = await repo.GetCandidateServersAsync(resolved, ct).ConfigureAwait(false);
        if (resolved.ProjectServerId is null && resolved.EnvironmentId is null && servers.Count > 1)
        {
            logger.LogWarning(
                "Library '{Library}' is owned by project {ProjectId} and reaches {Count} servers: its PORT_* entries are not registered, "
                + "which of them it deploys to is unknown. Attach it to an environment or a project server.",
                libraryName, projectId, servers.Count);
            return claims;
        }

        foreach (var (serverId, serverName) in servers)
            foreach (var entry in entries)
                claims.TryAdd(new ClaimKey(projectId, serverId, entry.Port), new ClaimDetail(serverName, entry.Key));
        return claims;
    }
}
