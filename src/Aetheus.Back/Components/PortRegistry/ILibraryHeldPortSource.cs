// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.PortRegistry;

/// <summary>
/// The ports a project's variable libraries still write for one server. A launch declares only the ports
/// of its own run and releases the project's other declared ports on that server; without this, the deploy
/// run of a project released the ports another of its libraries (the nightly's) still names, and the
/// allocator handed them to another project again. Implemented above this module, where libraries are known.
/// </summary>
public interface ILibraryHeldPortSource
{
    /// <summary>The ports of the <c>PORT_*</c> entries of <paramref name="projectId"/>'s libraries that reach
    /// <paramref name="serverId"/>.</summary>
    Task<IReadOnlyCollection<int>> GetHeldPortsAsync(int projectId, int serverId, CancellationToken ct = default);
}
