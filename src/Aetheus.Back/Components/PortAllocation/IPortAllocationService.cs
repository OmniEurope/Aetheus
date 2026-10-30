// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.PortAllocation;

/// <summary>Coordinates port reservations and library entries above their separate storage modules.
/// Relational providers use a re-entrant transaction, while the InMemory provider uses
/// compensation.</summary>
public interface IPortAllocationService
{
    /// <summary>
    /// The servers a library may allocate on (a server attached to an environment of the library's
    /// project counts as one of the project's), with the one to pre-select when exactly one is reachable.
    /// </summary>
    Task<PortAllocationTargetsDto> GetTargetsAsync(int libraryId, CancellationToken ct = default);

    /// <summary>
    /// Reserves one port per key and writes the entries; a failed write releases the ports the call had
    /// just reserved, so a reservation never outlives it. The reservation is recorded under the
    /// library's project, so the project's own pipeline is not later refused the ports allocated for
    /// it; a library with no project is refused rather than reserved under an identity no deployment
    /// would recognise.
    /// </summary>
    Task<List<VariableEntryDto>> AllocateIntoLibraryAsync(
        int libraryId, int serverId, IReadOnlyList<string> keys, CancellationToken ct = default);

    /// <summary>The project the library belongs to, directly or through its environment or project server;
    /// null when it has none or does not exist.</summary>
    Task<int?> GetLibraryProjectIdAsync(int libraryId, CancellationToken ct = default);

    /// <summary>The servers <see cref="ReleaseLibraryPortAsync"/> releases on: every server the library
    /// reaches, none for a library of no project. A missing library throws <see cref="NotFoundException"/>.</summary>
    Task<List<int>> GetReleaseServerIdsAsync(int libraryId, CancellationToken ct = default);

    /// <summary>Checks the library's existing <c>PORT_*</c> entries against one server.</summary>
    Task<PortCheckResultDto> CheckLibraryPortsAsync(
        int libraryId, int serverId, CancellationToken ct = default);

    /// <summary>
    /// Releases one port on every server the library reaches, but only where the library's own project
    /// holds it. Offered after a <c>PORT_*</c> entry is deleted, so the reservation does not outlive the
    /// variable and keep the port blocked for everybody. Returns how many reservations were released,
    /// which is zero when the port belongs to somebody else - that release is a deliberate act on the
    /// registry page, not a side effect of editing a library.
    /// </summary>
    Task<int> ReleaseLibraryPortAsync(int libraryId, int port, CancellationToken ct = default);
}
