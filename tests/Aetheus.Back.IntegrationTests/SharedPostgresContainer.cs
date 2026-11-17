// SPDX-License-Identifier: EUPL-1.2
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Aetheus.Back.IntegrationTests.SharedPostgresContainer))]

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// DRPS: a SINGLE PostgreSQL container shared by the whole assembly. It migrates one template database
/// once; every relational test class then gets its own database cloned from it (<see cref="PostgresFixture"/>,
/// <c>aetheus_it_N</c>), and the additive API-surface sweep (<see cref="ApiSmokeCollection"/>) uses
/// <c>aetheus_smoke</c>. No two classes share a database, so xUnit runs the classes in parallel, yet only
/// ONE Postgres boots. Exposed as an xUnit.v3 assembly fixture so every class and collection fixture
/// consumes the same container.
/// </summary>
public sealed class SharedPostgresContainer : IAsyncLifetime
{
    private const string AdminDatabase = "aetheus_it";
    private const string SmokeDatabase = "aetheus_smoke";

    // Pinned to the exact production multi-platform digest (deploy/compose/remote.compose.yml) so tests
    // exercise the same Postgres, not EF InMemory (which skips relational migrations + FK enforcement).
    internal const string Image = "postgres:18-alpine@sha256:9a8afca54e7861fd90fab5fdf4c42477a6b1cb7d293595148e674e0a3181de15";

    // Test classes run in parallel (one per processor by default), and every one of them may boot a real
    // host whose pools and background services hold several connections each: the default 100 is too few.
    private readonly PostgreSqlContainer _container = RunLabels(new PostgreSqlBuilder(
            Image)
        .WithDatabase(AdminDatabase)
        .WithUsername("aetheus")
        .WithPassword("aetheus_shared_pwd")
        .WithCommand("-c", "max_connections=1000"))
        .Build();

    /// <summary>
    /// The pipeline step disables Ryuk (a privileged, Docker-socket-mounting reaper that trips the container
    /// risk alert) and sets these two variables, so it can remove this run's containers itself and the stale
    /// ones of a killed run. A local run (Ryuk enabled, variables unset) adds no label.
    /// The VPS bench (<c>ylaunch -vb</c>) also limits this container like the rest of its containers, through
    /// <c>AETHEUS_TESTCONTAINERS_POSTGRES_CPUSET</c>, <c>_CPUS</c> and <c>_MEMORY_MB</c>; unset, nothing is limited.
    /// </summary>
    internal static PostgreSqlBuilder RunLabels(PostgreSqlBuilder builder)
    {
        var run = Environment.GetEnvironmentVariable("AETHEUS_TESTCONTAINERS_RUN");
        var startedAt = Environment.GetEnvironmentVariable("AETHEUS_TESTCONTAINERS_STARTED_AT");
        var labelled = string.IsNullOrEmpty(run) || string.IsNullOrEmpty(startedAt)
            ? builder
            : builder.WithLabel("aetheus.testcontainers.run", run).WithLabel("aetheus.testcontainers.started-at", startedAt);
        return WithBenchLimits(labelled);
    }

    private static PostgreSqlBuilder WithBenchLimits(PostgreSqlBuilder builder)
    {
        var cpuSet = Environment.GetEnvironmentVariable("AETHEUS_TESTCONTAINERS_POSTGRES_CPUSET");
        var cpus = double.TryParse(Environment.GetEnvironmentVariable("AETHEUS_TESTCONTAINERS_POSTGRES_CPUS"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedCpus) ? parsedCpus : 0;
        var memoryMb = long.TryParse(Environment.GetEnvironmentVariable("AETHEUS_TESTCONTAINERS_POSTGRES_MEMORY_MB"),
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedMemory) ? parsedMemory : 0;
        if (string.IsNullOrEmpty(cpuSet) && cpus <= 0 && memoryMb <= 0)
            return builder;
        return builder.WithCreateParameterModifier(parameters =>
        {
            ArgumentNullException.ThrowIfNull(parameters.HostConfig);
            if (!string.IsNullOrEmpty(cpuSet))
                parameters.HostConfig.CpusetCpus = cpuSet;
            if (cpus > 0)
                parameters.HostConfig.NanoCPUs = (long)(cpus * 1_000_000_000);
            if (memoryMb > 0)
            {
                parameters.HostConfig.Memory = memoryMb * 1024 * 1024;
                parameters.HostConfig.MemorySwap = memoryMb * 1024 * 1024;
            }
        });
    }

    /// <summary>Connection string for <paramref name="database"/> in this container.</summary>
    public string DatabaseConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }.ConnectionString;

    /// <summary>Connection string for the API-smoke database (<c>aetheus_smoke</c>) in the same container.</summary>
    public string SmokeConnectionString => DatabaseConnectionString(SmokeDatabase);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        // Migrate once, then let every relational test class clone this immutable database. PostgreSQL's
        // native template copy is substantially cheaper than replaying the complete EF migration chain.
        await PostgresDatabaseTemplate.InitializeAsync(_container.GetConnectionString());

        // Create the smoke database in the same instance so the smoke suite never shares a schema with
        // the relational suites - no cross-suite wipe despite a single container.
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE {SmokeDatabase};";
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
