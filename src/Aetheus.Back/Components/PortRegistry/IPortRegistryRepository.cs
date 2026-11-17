// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.PortRegistry;

public interface IPortRegistryRepository
{
    Task<List<ServerPortReservation>> GetReservationsAsync(int serverId, CancellationToken ct = default);

    /// <summary>Only the rows claiming one of <paramref name="ports"/> on that server.</summary>
    Task<List<ServerPortReservation>> GetReservationsForPortsAsync(
        int serverId, IReadOnlyCollection<int> ports, CancellationToken ct = default);

    /// <summary>Tracked, for the declaration upsert: the rows of one source that must be updated in place.</summary>
    Task<List<ServerPortReservation>> GetTrackedReservationsForPortsAsync(
        int serverId, IReadOnlyCollection<int> ports, PortReservationSource source, CancellationToken ct = default);

    /// <summary>Tracked, for the declaration upsert: the rows this owner still holds elsewhere on the server.</summary>
    Task<List<ServerPortReservation>> GetTrackedReservationsForOwnerAsync(
        int serverId, string ownerKey, PortReservationSource source, CancellationToken ct = default);

    /// <summary>Tracked: every manual row one owner holds, whatever the server, for its port sync.</summary>
    Task<List<ServerPortReservation>> GetTrackedManualReservationsForOwnerAsync(
        string ownerKey, CancellationToken ct = default);

    /// <summary>Tracked: every row a project holds, whatever the server, for its deletion cleanup.</summary>
    Task<List<ServerPortReservation>> GetTrackedReservationsForProjectAsync(
        string ownerKey, CancellationToken ct = default);

    /// <summary>Tracked: every <c>Observed</c> row of the server, which one scan replaces wholesale.</summary>
    Task<List<ServerPortReservation>> GetTrackedObservedReservationsAsync(
        int serverId, CancellationToken ct = default);

    /// <summary>Stamps the server's last port scan. Returns false when the server no longer exists.</summary>
    Task<bool> SetPortsObservedAtAsync(int serverId, DateTime observedAt, CancellationToken ct = default);

    /// <summary>Every port already taken on that server, whatever the source claiming it.</summary>
    Task<List<int>> GetTakenPortsAsync(int serverId, CancellationToken ct = default);

    /// <summary>The server's allocation window, or null when it uses the fleet default.</summary>
    Task<ServerPortRange?> FindPortRangeAsync(int serverId, CancellationToken ct = default);

    void TrackPortRange(ServerPortRange range);

    Task<ServerPortReservation?> FindReservationAsync(int id, CancellationToken ct = default);

    Task<string?> FindServerNameAsync(int serverId, CancellationToken ct = default);

    /// <summary>The server's last port scan and whether its agent can run one, or null when unknown.</summary>
    Task<(DateTime? LastScanAt, bool ObservationAvailable)?> FindObservationStateAsync(
        int serverId, CancellationToken ct = default);

    Task<bool> ServerExistsAsync(int serverId, CancellationToken ct = default);

    void TrackReservation(ServerPortReservation reservation);

    void RemoveReservation(ServerPortReservation reservation);

    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// BACK-008: runs one claim (read the holders, then write) under a PostgreSQL transaction advisory lock on
    /// the server, so two claims of different sources on that server cannot both read a free port and both
    /// write it. Joins an ambient transaction instead of opening a nested one; the lock is released when the
    /// transaction ends. The InMemory provider has no locks and runs the claim directly.
    /// </summary>
    Task<T> RunUnderServerClaimLockAsync<T>(int serverId, Func<Task<T>> claim, CancellationToken ct = default);
}
