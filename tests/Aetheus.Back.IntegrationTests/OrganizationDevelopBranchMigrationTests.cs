// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-76 (owner decision 2026-10-28): <c>AddOrganizationAutoCreateDevelopBranch</c> run for real on PostgreSQL from the migration
/// before it, over organizations written in SQL with the schema of that point. The platform organization (slug
/// <c>aetheus</c>) ends with the setting on, every other organization keeps the default (off).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OrganizationDevelopBranchMigrationTests(PostgresFixture fixture)
{
    private const string Migration = "20261029072029_AddOrganizationAutoCreateDevelopBranch";

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;

    [Fact]
    public async Task OnlyThePlatformOrganization_StartsWithTheSettingOn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var db = new AppDbContext(Options()))
        {
            await db.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct);
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.IndexOf(Migration);
            Assert.True(index > 0, $"Could not locate {Migration} after a predecessor.");
            await db.GetService<IMigrator>().MigrateAsync(migrations[index - 1], ct);
            // The platform organization "Aetheus" is created by an earlier migration (AddOrganizationOwnership): it is
            // already there, next to the two rows written here.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Organizations" ("Name", "Slug", "Description", "CreatedAt", "UpdatedAt")
                VALUES ('Acme', 'acme', 'customer', now(), now()),
                       ('Aetheus Labs', 'aetheus-labs', 'looks alike', now(), now())
                """, ct);
        }

        await using (var db = new AppDbContext(Options()))
            await db.GetService<IMigrator>().MigrateAsync(Migration, ct);

        await using (var db = new AppDbContext(Options()))
        {
            Assert.Contains(Migration, await db.Database.GetAppliedMigrationsAsync(ct));
            var settings = await db.Organizations.AsNoTracking().OrderBy(o => o.Slug)
                .ToDictionaryAsync(o => o.Slug, o => o.AutoCreateDevelopBranch, ct);
            Assert.Equal(
                new Dictionary<string, bool> { ["acme"] = false, ["aetheus"] = true, ["aetheus-labs"] = false },
                settings);
        }

        // The rows above must not leak into the collection's next host: start it clean.
        await using var reset = new AppDbContext(Options());
        await reset.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct);
        await reset.Database.MigrateAsync(ct);
    }
}
