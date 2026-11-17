// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PortRegistry;

namespace Aetheus.Back.Components.PortAllocation;

/// <summary>Reads, for one project and one server, the ports its libraries' <c>PORT_*</c> entries name, with the
/// same library-to-server mapping the registrar uses to declare them.</summary>
internal sealed class LibraryHeldPortSource(IPortAllocationRepository repo) : ILibraryHeldPortSource
{
    public async Task<IReadOnlyCollection<int>> GetHeldPortsAsync(int projectId, int serverId, CancellationToken ct = default)
    {
        var held = new HashSet<int>();
        foreach (var source in await repo.GetPortLibrariesAsync(projectId, ct).ConfigureAwait(false))
        {
            var scope = await repo.ResolveScopeAsync(source.LibraryId, source.LibraryName, source.Scope, ct)
                .ConfigureAwait(false);
            var servers = await repo.GetCandidateServersAsync(scope, ct).ConfigureAwait(false);
            if (servers.Exists(server => server.ServerId == serverId))
                held.UnionWith(source.Entries.Select(entry => entry.Port));
        }

        return held;
    }
}
