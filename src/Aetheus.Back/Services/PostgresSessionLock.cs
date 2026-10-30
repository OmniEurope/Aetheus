// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services;

/// <summary>
/// A PostgreSQL session advisory lock (ADR-029) held on the context's connection, kept open until the
/// returned handle is disposed: a caller holds it until it has saved, so the next request that takes the
/// same lock reads the committed result. Used for invariants a single count cannot keep under
/// concurrency, such as an organization never losing its last Owner (RBAC audit, finding 10). The
/// InMemory provider has no locks, so it gets a handle that does nothing.
/// </summary>
internal static class PostgresSessionLock
{
    private const string LockSql = "SELECT pg_advisory_lock(hashtextextended({0}, 0))";

    internal static async Task<IAsyncDisposable> AcquireAsync(AppDbContext db, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!db.Database.IsRelational()) return NoLock.Instance;
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await db.Database.ExecuteSqlRawAsync(LockSql, [key], ct).ConfigureAwait(false);
        }
        catch
        {
            // The lock was not taken: the connection opened for it must not stay open.
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            throw;
        }
        return new Release(db, key);
    }

    // Carries its own unlock statement so the handle never refers back to the lock type that creates it.
    private sealed class Release(AppDbContext db, string key) : IAsyncDisposable
    {
        private const string UnlockSql = "SELECT pg_advisory_unlock(hashtextextended({0}, 0))";

        public async ValueTask DisposeAsync()
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(UnlockSql, [key]).ConfigureAwait(false);
            }
            finally
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class NoLock : IAsyncDisposable
    {
        public static readonly NoLock Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
