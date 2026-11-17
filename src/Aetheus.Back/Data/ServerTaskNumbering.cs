// SPDX-License-Identifier: EUPL-1.2
using System.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace Aetheus.Back.Data;

/// <summary>
/// ADR-060: every task inserted takes the next number of its server, whichever of the many task
/// factories built it, so the reservation sits in <c>AppDbContext</c> and not in each of them.
/// </summary>
internal static class ServerTaskNumbering
{
    internal static bool HasNewTasks(DbContext db) =>
        db.ChangeTracker.Entries<ServerTask>().Any(entry => entry.State == EntityState.Added);

    /// <summary>
    /// Opens the transaction that keeps the server rows locked until the new tasks are committed, or
    /// null when the caller already owns one (or the provider has none). A number is therefore either
    /// committed with its task or given back, and a backfill holding the lock never sees a reserved
    /// number as free.
    /// </summary>
    internal static async Task<IDbContextTransaction?> BeginAsync(DbContext db, CancellationToken ct) =>
        db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false)
            : null;

    /// <summary>
    /// Numbers the added tasks. Servers are locked in ascending order, so two saves on the same servers
    /// cannot deadlock each other. A retried save reserves again: its first numbers were given back with
    /// the rolled-back transaction, or stay unused under a caller's transaction.
    /// </summary>
    internal static async Task ReserveAsync(DbContext db, CancellationToken ct)
    {
        var perServer = db.ChangeTracker.Entries<ServerTask>()
            .Where(entry => entry.State == EntityState.Added)
            .GroupBy(entry => entry.Property(task => task.ServerId).CurrentValue)
            .OrderBy(group => group.Key)
            .ToList();

        foreach (var tasks in perServer)
        {
            var added = tasks.ToList();
            if (await ReserveRangeAsync(db, tasks.Key, added.Count, ct).ConfigureAwait(false) is not { } highest)
                continue;

            var number = highest - added.Count;
            foreach (var entry in added)
                entry.Entity.Number = ++number;
        }
    }

    private static async Task<int?> ReserveRangeAsync(DbContext db, int serverId, int count, CancellationToken ct)
    {
        // A server saved with its first task has no row to lock yet: its counter is inserted with it.
        if (FindAdded(db, serverId) is { } newServer)
            return newServer.Entity.TaskCounter += count;

        var servers = AllServers(db).Where(server => server.Id == serverId);
        if (!db.Database.IsRelational())
        {
            // InMemory (unit tests): no bulk update and no row locks, so increment the tracked entity.
            var tracked = await servers.FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return tracked is null ? null : tracked.TaskCounter += count;
        }

        // The UPDATE takes the row lock; the read-back after it sees this transaction's value.
        var rows = await servers
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(server => server.TaskCounter, server => server.TaskCounter + count),
                ct)
            .ConfigureAwait(false);
        if (rows == 0) return null;

        return await servers.AsNoTracking()
            .Select(server => server.TaskCounter)
            .SingleAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>A retired server keeps running its cleanup tasks, so the filter does not apply here.</summary>
    internal static IQueryable<Server> AllServers(DbContext db) =>
        db.Set<Server>().IgnoreQueryFilters([ServerQueryFilters.ExcludeRetired]);

    private static EntityEntry<Server>? FindAdded(DbContext db, int serverId) =>
        db.ChangeTracker.Entries<Server>().FirstOrDefault(entry =>
            entry.State == EntityState.Added && entry.Property(server => server.Id).CurrentValue == serverId);
}
