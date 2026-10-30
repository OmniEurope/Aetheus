// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-58: the <c>AddRegistryPackageAnonymousDownload</c> migration, run for real on PostgreSQL. Only the
/// existing NuGet <c>Aetheus.Telemetry</c> and <c>Aetheus.WebAnalytics</c> and the npm
/// <c>@aetheus/web-analytics</c> are opened; another package, or the same name under another kind,
/// stays private.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RegistryPackageAnonymousDownloadMigrationTests(PostgresFixture fixture)
{
    private const string Migration = "20261028060850_AddRegistryPackageAnonymousDownload";

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;

    [Fact]
    public async Task Up_OpensOnlyTheObservabilityPackages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var reset = new AppDbContext(Options()))
            await reset.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct);

        var ids = new Dictionary<string, int>();
        await using (var db = new AppDbContext(Options()))
        {
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.IndexOf(Migration);
            Assert.True(index > 0, $"Could not locate {Migration} after a predecessor.");
            await db.GetService<IMigrator>().MigrateAsync(migrations[index - 1], ct);

            // Raw rows: at this migration point the table has no AllowAnonymousDownload column.
            foreach (var (key, kind, name, normalized) in new[]
            {
                ("nuget-telemetry", 0, "Aetheus.Telemetry", "aetheus.telemetry"),
                ("nuget-webanalytics", 0, "Aetheus.WebAnalytics", "aetheus.webanalytics"),
                ("npm-webanalytics", 1, "@aetheus/web-analytics", "@aetheus/web-analytics"),
                ("nuget-other", 0, "Aetheus.Agent.Core", "aetheus.agent.core"),
                ("npm-same-name", 1, "aetheus.telemetry", "aetheus.telemetry"),
                ("maven-same-name", 2, "aetheus.webanalytics", "aetheus.webanalytics"),
            })
            {
                ids[key] = (await db.Database.SqlQueryRaw<int>(
                    """
                    INSERT INTO "RegistryPackages" ("Kind", "Name", "NormalizedName", "CreatedAt", "UpdatedAt")
                    VALUES (@kind, @name, @normalized, now(), now())
                    RETURNING "Id" AS "Value"
                    """,
                    new NpgsqlParameter("kind", kind),
                    new NpgsqlParameter("name", name),
                    new NpgsqlParameter("normalized", normalized)).ToListAsync(ct)).Single();
            }
        }

        await using (var db = new AppDbContext(Options()))
            await db.Database.MigrateAsync(ct);

        await using (var db = new AppDbContext(Options()))
        {
            var flags = await db.RegistryPackages.AsNoTracking()
                .ToDictionaryAsync(package => package.Id, package => package.AllowAnonymousDownload, ct);
            Assert.True(flags[ids["nuget-telemetry"]]);
            Assert.True(flags[ids["nuget-webanalytics"]]);
            Assert.True(flags[ids["npm-webanalytics"]]);
            Assert.False(flags[ids["nuget-other"]]);
            Assert.False(flags[ids["npm-same-name"]]);
            Assert.False(flags[ids["maven-same-name"]]);
        }
    }
}
