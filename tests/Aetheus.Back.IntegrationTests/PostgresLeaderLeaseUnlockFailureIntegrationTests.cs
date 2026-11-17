// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Audit follow-up, now on the only advisory-lock path (ADR-029, audit E4): when <c>pg_advisory_unlock</c> fails
/// after a serialized operation, the session still holds the lock. Its connection must be destroyed rather than
/// returned to the pool, otherwise the next request that takes the same name (the organization owners, the
/// administrator set) waits forever.
/// </summary>
public sealed class PostgresLeaderLeaseUnlockFailureIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Schema = "e4_unlock_failure";

    private const string CreateFailingUnlockSql = """
        CREATE SCHEMA IF NOT EXISTS e4_unlock_failure;
        CREATE OR REPLACE FUNCTION e4_unlock_failure.pg_advisory_unlock(bigint) RETURNS boolean LANGUAGE plpgsql
            AS $$ BEGIN RAISE EXCEPTION 'Test: unlock failed'; END $$;
        """;

    private const string DropSchemaSql = "DROP SCHEMA IF EXISTS e4_unlock_failure CASCADE";

    /// <summary>Records whether the lease said it invalidated its pool after a failed release.</summary>
    private sealed class ReleaseFailures : ILogger<PostgresLeaderLease>
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("invalidated its connection pool", StringComparison.Ordinal))
                Interlocked.Increment(ref _count);
        }
    }

    [Fact]
    public async Task AFailedUnlock_DestroysTheConnection_SoTheLockIsReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = "integration:unlock-failure:" + Guid.NewGuid().ToString("N");
        await using var other = new NpgsqlConnection(fixture.ConnectionString);
        await other.OpenAsync(ct);
        // A pg_advisory_unlock(bigint) that always fails, found before pg_catalog's by the lease's connections only:
        // the lock statement is the real one, the unlock fails on the server as an unprovable release would.
        await using (var create = new NpgsqlCommand(CreateFailingUnlockSql, other))
            await create.ExecuteNonQueryAsync(ct);
        try
        {
            // No reset on close: a connection wrongly returned to the pool would keep the lock for good.
            var leaseConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                SearchPath = Schema + ",pg_catalog",
                NoResetOnClose = true
            }.ConnectionString;
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = leaseConnectionString })
                .Build();
            var failures = new ReleaseFailures();
            var lease = new PostgresLeaderLease(configuration, failures);
            var ran = false;

            await lease.RunSerializedAsync(name, _ =>
            {
                ran = true;
                return Task.CompletedTask;
            }, ct);

            Assert.True(ran);
            Assert.Equal(1, failures.Count);

            // A destroyed connection frees its lock when the server ends the backend, which it does on its own time
            // under load. A connection wrongly returned to the pool never frees it, so polling for a generous
            // deadline keeps the assertion exactly as strong.
            var taken = false;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!taken && DateTime.UtcNow < deadline)
            {
                taken = await TryLockAsync(other, name, ct);
                if (!taken) await Task.Delay(100, ct);
            }

            Assert.True(taken, "the lock was still held by a connection returned to the pool (30 s)");
            await using var unlockAll = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", other);
            await unlockAll.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await using var drop = new NpgsqlCommand(DropSchemaSql, other);
            await drop.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<bool> TryLockAsync(NpgsqlConnection connection, string name, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@name, 0))", connection);
        command.Parameters.AddWithValue("name", name);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }
}
