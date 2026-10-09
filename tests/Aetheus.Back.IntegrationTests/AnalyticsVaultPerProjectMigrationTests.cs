// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// PLAN-003 2.1: the <c>AnalyticsVaultPerProject</c> data migration, run for real on PostgreSQL. Two
/// apps of one project and an orphan vault, as production has them: each app ends up with its own key
/// in <c>&lt;project&gt;.analytics</c>, carrying the same encrypted value, and the legacy vaults are left
/// as they were.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalyticsVaultPerProjectMigrationTests(PostgresFixture fixture)
{
    private const string Migration = "20260912184518_AnalyticsVaultPerProject";

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;

    [Fact]
    public async Task Up_MovesEachAppsKeyIntoItsProjectVault_AndLeavesTheLegacyVaultsUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var reset = new AppDbContext(Options()))
            await reset.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct);

        int projectId, firstAppId, secondAppId, soloProjectId, soloAppId;
        await using (var db = new AppDbContext(Options()))
        {
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.IndexOf(Migration);
            Assert.True(index > 0, $"Could not locate {Migration} after a predecessor.");
            await db.GetService<IMigrator>().MigrateAsync(migrations[index - 1], ct);

            var organizationId = await db.Organizations.OrderBy(o => o.Id).Select(o => o.Id).FirstAsync(ct);
            projectId = await LegacyProjectRows.InsertAsync(db, "Mon Aetheus", organizationId, ct);

            firstAppId = await InsertLegacyAppAsync(db, projectId, "front", ct);
            secondAppId = await InsertLegacyAppAsync(db, projectId, "docs", ct);

            soloProjectId = await LegacyProjectRows.InsertAsync(db, "Portfolio", organizationId, ct);
            soloAppId = await InsertLegacyAppAsync(db, soloProjectId, "portfolio", ct);

            db.Vaults.AddRange(
                LegacyVault(soloProjectId, $"aetheus-web-analytics-{soloAppId}", "cipher-solo"),
                LegacyVault(projectId, $"aetheus-web-analytics-{firstAppId}", "cipher-first"),
                LegacyVault(projectId, $"aetheus-web-analytics-{secondAppId}", "cipher-second"),
                LegacyVault(projectId, "aetheus-web-analytics-999", "cipher-orphan"));
            await db.SaveChangesAsync(ct);
        }

        await using (var db = new AppDbContext(Options()))
        {
            await db.Database.MigrateAsync(ct);
            // Applying it twice must add nothing: the deploy path re-runs migrations on every boot.
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '" + Migration + "';", ct);
            await db.Database.MigrateAsync(ct);
        }

        await using (var db = new AppDbContext(Options()))
        {
            var apps = await db.MonitoredApps.AsNoTracking().Where(a => a.ProjectId == projectId).ToListAsync(ct);
            Assert.Equal(2, apps.Count);
            Assert.All(apps, app => Assert.Equal("mon-aetheus.analytics", app.AnalyticsVaultName));
            Assert.Equal("mon-aetheus.analytics", AppWebAnalyticsConfigurationService.VaultNameFor("Mon Aetheus", projectId));

            var target = await db.Vaults.AsNoTracking().Include(v => v.Secrets).ThenInclude(s => s.Versions)
                .SingleAsync(v => v.Name == "mon-aetheus.analytics", ct);
            Assert.Equal(projectId, target.ProjectId);
            var expected = new[]
            {
                (Key: AppWebAnalyticsConfigurationService.SecretKeyFor(firstAppId), Cipher: "cipher-first"),
                (Key: AppWebAnalyticsConfigurationService.SecretKeyFor(secondAppId), Cipher: "cipher-second")
            }.OrderBy(pair => pair.Key, StringComparer.Ordinal);
            Assert.Equal(
                expected,
                target.Secrets.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => (Key: s.Key, Cipher: s.EncryptedValue)));
            Assert.All(target.Secrets, secret => Assert.Single(secret.Versions));

            // One analytics app in the project: the previous release, which reads the unsuffixed key
            // wherever AnalyticsVaultName points, finds the same value there.
            var soloApp = await db.MonitoredApps.AsNoTracking().SingleAsync(a => a.Id == soloAppId, ct);
            Assert.Equal("portfolio.analytics", soloApp.AnalyticsVaultName);
            var soloVault = await db.Vaults.AsNoTracking().Include(v => v.Secrets)
                .SingleAsync(v => v.Name == "portfolio.analytics" && v.ProjectId == soloProjectId, ct);
            Assert.Equal(
                [
                    (AppWebAnalyticsConfigurationService.SecretKey, "cipher-solo"),
                    (AppWebAnalyticsConfigurationService.SecretKeyFor(soloAppId), "cipher-solo")
                ],
                soloVault.Secrets.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => (s.Key, s.EncryptedValue)));

            var legacy = await db.Vaults.AsNoTracking().Include(v => v.Secrets)
                .Where(v => v.Name.StartsWith("aetheus-web-analytics-")).ToListAsync(ct);
            Assert.Equal(4, legacy.Count);
            Assert.All(legacy, vault => Assert.Equal(
                AppWebAnalyticsConfigurationService.SecretKey, Assert.Single(vault.Secrets).Key));
        }
    }

    /// <summary>
    /// Inserts an app with SQL written against the schema the database is at, not with the current EF
    /// model: the model gains MonitoredApp columns the predecessor migration never created (recette
    /// R2-013 added two), and an INSERT naming them fails on 42703. Every NOT NULL column without a
    /// default gets the zero value of its type; the test only cares about the project, the name and
    /// the legacy vault name.
    /// </summary>
    private static async Task<int> InsertLegacyAppAsync(AppDbContext db, int projectId, string name, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);

        var required = new List<(string Column, string Type)>();
        await using (var columns = connection.CreateCommand())
        {
            columns.CommandText =
                "SELECT column_name, data_type FROM information_schema.columns " +
                "WHERE table_name = 'MonitoredApps' AND is_nullable = 'NO' AND column_default IS NULL " +
                "AND is_identity = 'NO' AND column_name NOT IN ('ProjectId', 'Name')";
            await using var reader = await columns.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) required.Add((reader.GetString(0), reader.GetString(1)));
        }

        var names = new List<string> { "\"ProjectId\"", "\"Name\"", "\"AnalyticsVaultName\"" };
        var values = new List<string> { "@project", "@name", "@vault" };
        foreach (var (column, type) in required)
        {
            names.Add($"\"{column}\"");
            values.Add(type switch
            {
                "boolean" => "false",
                "integer" or "bigint" or "smallint" or "numeric" or "double precision" or "real" => "0",
                _ when type.StartsWith("timestamp", StringComparison.Ordinal) => "now()",
                _ => "''"
            });
        }

        await using var insert = connection.CreateCommand();
        // The column names come from information_schema of the test database and the literals from the
        // switch above; the only caller-supplied values travel as parameters.
#pragma warning disable CA2100
        insert.CommandText =
            $"INSERT INTO \"MonitoredApps\" ({string.Join(", ", names)}) VALUES ({string.Join(", ", values)}) RETURNING \"Id\"";
#pragma warning restore CA2100
        AddParameter(insert, "@project", projectId);
        AddParameter(insert, "@name", name);
        AddParameter(insert, "@vault", "pending");
        var id = Convert.ToInt32(await insert.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);

        await using var vault = connection.CreateCommand();
        vault.CommandText = "UPDATE \"MonitoredApps\" SET \"AnalyticsVaultName\" = @vault WHERE \"Id\" = @id";
        AddParameter(vault, "@vault", $"aetheus-web-analytics-{id}");
        AddParameter(vault, "@id", id);
        await vault.ExecuteNonQueryAsync(ct);
        return id;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static Vault LegacyVault(int projectId, string name, string cipher) => new()
    {
        Name = name,
        ProjectId = projectId,
        Secrets = [new VaultSecret { Key = AppWebAnalyticsConfigurationService.SecretKey, EncryptedValue = cipher }]
    };
}
