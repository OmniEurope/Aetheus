// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Aetheus.Back.Components.PortRegistry;

public class PortRegistryRepository(AppDbContext db) : IPortRegistryRepository
{
    public async Task<List<ServerPortReservation>> GetReservationsAsync(int serverId, CancellationToken ct = default)
    {
        // The project is joined here only: the list is the one place that shows a holder's project by
        // name, and a reservation row without it would force the UI to print a bare id.
        return await db.ServerPortReservations
            .AsNoTracking()
            .Include(reservation => reservation.Project)
            .Where(reservation => reservation.ServerId == serverId)
            .OrderBy(reservation => reservation.Port)
            .ThenBy(reservation => reservation.Source)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<ServerPortReservation>> GetReservationsForPortsAsync(
        int serverId, IReadOnlyCollection<int> ports, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ports);
        if (ports.Count == 0) return [];
        var wanted = ports.ToList();
        return await db.ServerPortReservations
            .AsNoTracking()
            .Where(reservation => reservation.ServerId == serverId && wanted.Contains(reservation.Port))
            .OrderBy(reservation => reservation.Port)
            .ThenBy(reservation => reservation.Source)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<ServerPortReservation>> GetTrackedReservationsForPortsAsync(
        int serverId, IReadOnlyCollection<int> ports, PortReservationSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ports);
        if (ports.Count == 0) return [];
        var wanted = ports.ToList();
        return await db.ServerPortReservations
            .Where(reservation => reservation.ServerId == serverId
                && reservation.Source == source
                && wanted.Contains(reservation.Port))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<ServerPortReservation>> GetTrackedReservationsForOwnerAsync(
        int serverId, string ownerKey, PortReservationSource source, CancellationToken ct = default)
    {
        return await db.ServerPortReservations
            .Where(reservation => reservation.ServerId == serverId
                && reservation.Source == source
                && reservation.OwnerKey == ownerKey)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<ServerPortReservation>> GetTrackedManualReservationsForOwnerAsync(
        string ownerKey, CancellationToken ct = default)
    {
        return await db.ServerPortReservations
            .Where(reservation => reservation.OwnerKey == ownerKey
                && reservation.Source == PortReservationSource.Manual)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<ServerPortReservation>> GetTrackedReservationsForProjectAsync(
        string ownerKey, CancellationToken ct = default)
    {
        return await db.ServerPortReservations
            .Where(reservation => reservation.OwnerKey == ownerKey
                && reservation.Source != PortReservationSource.Observed)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<ServerPortReservation>> GetTrackedObservedReservationsAsync(
        int serverId, CancellationToken ct = default)
    {
        return await db.ServerPortReservations
            .Where(reservation => reservation.ServerId == serverId
                && reservation.Source == PortReservationSource.Observed)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> SetPortsObservedAtAsync(
        int serverId, DateTime observedAt, CancellationToken ct = default)
    {
        var server = await db.Servers
            .FirstOrDefaultAsync(candidate => candidate.Id == serverId, ct)
            .ConfigureAwait(false);
        if (server is null) return false;
        server.PortsObservedAt = observedAt;
        return true;
    }

    public async Task<List<int>> GetTakenPortsAsync(int serverId, CancellationToken ct = default)
    {
        // TCP only: an allocated port is handed to a deployment, which binds TCP. A host listening on
        // 10041/udp does not stop anything from binding 10041/tcp, and skipping it would burn a free
        // port for no reason.
        return await db.ServerPortReservations
            .AsNoTracking()
            .Where(reservation => reservation.ServerId == serverId
                && reservation.Protocol == PortRegistryLimits.TcpProtocol)
            .Select(reservation => reservation.Port)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<ServerPortRange?> FindPortRangeAsync(int serverId, CancellationToken ct = default)
    {
        return await db.ServerPortRanges
            .FirstOrDefaultAsync(range => range.ServerId == serverId, ct)
            .ConfigureAwait(false);
    }

    public void TrackPortRange(ServerPortRange range) => db.ServerPortRanges.Add(range);

    public async Task<ServerPortReservation?> FindReservationAsync(int id, CancellationToken ct = default)
    {
        return await db.ServerPortReservations
            .FirstOrDefaultAsync(reservation => reservation.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task<string?> FindServerNameAsync(int serverId, CancellationToken ct = default)
    {
        return await db.Servers
            .AsNoTracking()
            .Where(server => server.Id == serverId)
            .Select(server => server.Name)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<(DateTime? LastScanAt, bool ObservationAvailable)?> FindObservationStateAsync(
        int serverId, CancellationToken ct = default)
    {
        var state = await db.Servers
            .AsNoTracking()
            .Where(server => server.Id == serverId)
            .Select(server => new { server.PortsObservedAt, server.PortObservationAvailable })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return state is null ? null : (state.PortsObservedAt, state.PortObservationAvailable);
    }

    public async Task<bool> ServerExistsAsync(int serverId, CancellationToken ct = default)
    {
        return await db.Servers.AnyAsync(server => server.Id == serverId, ct).ConfigureAwait(false);
    }

    public void TrackReservation(ServerPortReservation reservation) =>
        db.ServerPortReservations.Add(reservation);

    public void RemoveReservation(ServerPortReservation reservation) =>
        db.ServerPortReservations.Remove(reservation);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

    // The unique index includes Source, so it cannot make a Declared and a Manual claim collide; this lock
    // serializes the claims of one server instead. Transaction-scoped and taken through the ADR-029 lease class
    // (audit E4): it holds until the outer transaction a library or allocation write joined commits.
    public async Task<T> RunUnderServerClaimLockAsync<T>(int serverId, Func<Task<T>> claim, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!db.Database.IsRelational()) return await claim().ConfigureAwait(false);

        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        var key = "aetheus:port-claims:" + serverId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await PostgresLeaderLease.LockForTransactionAsync(
            (NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction(),
            key, ct).ConfigureAwait(false);
        var result = await claim().ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }
}
