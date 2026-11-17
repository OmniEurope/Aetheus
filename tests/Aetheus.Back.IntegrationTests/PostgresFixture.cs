// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// One relational database per test class, on the shared assembly container. The container is owned by
/// <see cref="SharedPostgresContainer"/> (one Postgres for the whole assembly); each class that declares
/// <c>IClassFixture&lt;PostgresFixture&gt;</c> gets its own database, cloned from the migrated template, so
/// classes no longer share rows and xUnit runs them in parallel. Pinned to the production
/// <c>postgres:18-alpine</c> engine, so the tests exercise the same Postgres, not EF InMemory (which
/// silently skips relational migrations and FK enforcement).
/// </summary>
public sealed class PostgresFixture(SharedPostgresContainer shared) : IAsyncLifetime
{
    private static int s_lastDatabase;

    private string _connectionString = NextConnectionString(shared);

    /// <summary>Connection string of this class's current database (<c>aetheus_it_N</c>). Read it after
    /// <see cref="ResetAsync"/>: a reset moves the class to a new database.</summary>
    public string ConnectionString => Volatile.Read(ref _connectionString);

    /// <summary>
    /// Moves this class to a fresh copy of the migrated template and drops the previous database. A new name
    /// rather than the same one recreated: a pooled connection to the dropped database, whatever connection
    /// string variant a test built from <see cref="ConnectionString"/>, can then never be handed out again.
    /// </summary>
    public async Task ResetAsync()
    {
        var previous = ConnectionString;
        var next = NextConnectionString(shared);
        await PostgresDatabaseTemplate.CreateAsync(next);
        Volatile.Write(ref _connectionString, next);
        await PostgresDatabaseTemplate.DropAsync(previous);
    }

    public async ValueTask InitializeAsync() => await PostgresDatabaseTemplate.CreateAsync(ConnectionString);

    public async ValueTask DisposeAsync() => await PostgresDatabaseTemplate.DropAsync(ConnectionString);

    private static string NextConnectionString(SharedPostgresContainer shared) =>
        shared.DatabaseConnectionString($"aetheus_it_{Interlocked.Increment(ref s_lastDatabase)}");
}

/// <summary>
/// The classes that must not run beside any other class: xUnit runs this collection alone, after every
/// parallel collection. Each class still owns its <see cref="PostgresFixture"/> database; what it shares
/// with the others is process-wide or cluster-wide, and every member says which in a comment above its
/// collection attribute.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialPostgresCollection
{
    public const string Name = "Serial Postgres collection";
}
