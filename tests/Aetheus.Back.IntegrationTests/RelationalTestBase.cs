// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Base for tests that need a clean, fully-migrated <see cref="AppDbContext"/> bound to the
/// shared Testcontainers PostgreSQL instance - WITHOUT the application host (so DbInitializer
/// does not run and seed data does not pollute relational-constraint assertions).
/// <para>
/// The container is shared across the whole test run for speed, so every test restores a
/// database template on which the real EF migrations were already applied. This is the layer where the InMemory unit
/// suite is blind: InMemory neither applies relational migrations nor enforces unique
/// indexes, foreign keys, filtered indexes, or real transaction isolation.
/// </para>
/// </summary>
public abstract class RelationalTestBase(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    protected string ConnectionString => fixture.ConnectionString;

    protected DbContextOptions<AppDbContext> BuildOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

    protected AppDbContext NewContext() => new(BuildOptions());

    /// <summary>
    /// Restores the immutable, fully migrated PostgreSQL template so the test starts from a
    /// pristine, production-shaped relational schema without replaying every migration.
    /// </summary>
    protected async Task ResetAndMigrateAsync()
    {
        await fixture.ResetAsync();
    }
}
