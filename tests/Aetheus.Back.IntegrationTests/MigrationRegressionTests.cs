// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.CodeAnalysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Regression guard for the prod incident where a migration added a NON-NULL FK column
/// (<c>Projects.OrganizationId</c> → <c>Organizations</c>) and set existing rows to
/// <c>OrganizationId = 0</c> WITHOUT first seeding/backfilling a parent row. Production
/// crash-looped with PostgreSQL <c>23503</c> /
/// <c>FK_Projects_Organizations_OrganizationId, Key (OrganizationId)=(0) is not present</c>.
/// <para>
/// The unit suite (<c>Aetheus.Back.Tests</c>) stayed GREEN because it runs on EF InMemory,
/// which neither applies relational migrations nor enforces foreign keys. These tests run the
/// REAL migration pipeline against a REAL PostgreSQL container, so a re-introduction of a
/// non-backfilled non-null FK migration fails here.
/// </para>
/// <para>
/// On this branch <see cref="Project"/> already carries the non-null <c>OrganizationId</c> FK,
/// so a legacy row must be inserted with the organization the fixed
/// <c>AddOrganizationOwnership</c> migration seeds. The down→up test then drops and re-applies
/// that migration on top of the pre-existing row: with the seed/backfill it stays GREEN; revert
/// the backfill and it goes RED with 23503 - the exact signal missing during the incident.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MigrationRegressionTests(PostgresFixture fixture)
{
    private DbContextOptions<AppDbContext> BuildOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

    /// <summary>
    /// The organization the <c>AddOrganizationOwnership</c> migration seeds (slug "aetheus").
    /// These tests use a bare <see cref="AppDbContext"/> (not the app host), so DbInitializer
    /// does not run - the seeded org therefore comes purely from the migration's own SQL,
    /// which also proves that seed exists.
    /// </summary>
    private static async Task<int> SeededOrganizationIdAsync(AppDbContext db) =>
        await db.Organizations.OrderBy(o => o.Id).Select(o => o.Id).FirstAsync();

    /// <summary>
    /// Reads a single integer with the raw connection, for the tests that drive the database at an
    /// older migration point than the entities describe. Going through EF there would select columns
    /// that migration has not created yet.
    /// </summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test-only helper; every caller passes a literal query in this file and binds its values through NpgsqlParameter.")]
    private static async Task<int> ScalarAsync(AppDbContext db, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql);
        command.Connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (command.Connection.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// All migrations apply cleanly on an EMPTY database. This is the "fresh install" path.
    /// </summary>
    [Fact]
    public async Task Migrate_OnEmptyDatabase_AppliesAllMigrations_WithoutThrowing()
    {
        await ResetDatabaseAsync();
        await using var db = new AppDbContext(BuildOptions());

        var exception = await Record.ExceptionAsync(() => db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(exception);
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken)).ToList();
        Assert.NotEmpty(applied);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// THE priority regression test. Reproduces the production topology:
    /// <list type="number">
    /// <item>migrate to latest,</item>
    /// <item>insert a <c>Projects</c> row (existing production data),</item>
    /// <item>run <c>MigrateAsync()</c> AGAIN (the deploy path re-runs migrations on every boot).</item>
    /// </list>
    /// If a future migration adds a non-null FK on <c>Projects</c> and assigns a dangling parent id
    /// to that pre-existing row without seeding/backfilling the parent, step 3 throws a Postgres
    /// <c>23503</c> foreign-key violation and this test goes RED - exactly the signal that was
    /// missing when the incident shipped.
    /// </summary>
    [Fact]
    public async Task Migrate_WithPreExistingProjectRow_IsIdempotent_AndForeignKeysHold()
    {
        await ResetDatabaseAsync();

        await using (var db = new AppDbContext(BuildOptions()))
        {
            await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        // Simulate production data that predates any future schema change. Project carries a
        // non-null OrganizationId FK on this branch, so attach it to the org the migration seeded.
        int projectId;
        await using (var db = new AppDbContext(BuildOptions()))
        {
            var project = new Project
            {
                Name = "Legacy Project",
                Description = "Row that exists before the next migration runs",
                OrganizationId = await SeededOrganizationIdAsync(db),
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            projectId = project.Id;
        }

        // Re-run migrations against the populated DB (the entrypoint does this every deploy).
        await using (var db = new AppDbContext(BuildOptions()))
        {
            var exception = await Record.ExceptionAsync(() => db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken));

            Assert.Null(exception);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken));

            // The pre-existing row must survive the (re-)migration intact.
            var reloaded = await db.Projects.AsNoTracking()
                .SingleAsync(p => p.Id == projectId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Legacy Project", reloaded.Name);
        }
    }

    /// <summary>
    /// Down-then-up round trip on a populated database. This is the strongest generic guard:
    /// it forces every migration's <c>Up()</c> to run a second time on a database that already
    /// holds a <c>Projects</c> row. A non-backfilled non-null FK migration cannot survive this.
    /// </summary>
    [Fact]
    public async Task Migrate_DownToInitial_ThenUpToLatest_WithData_DoesNotViolateForeignKeys()
    {
        await ResetDatabaseAsync();
        await using var db = new AppDbContext(BuildOptions());

        var migrations = db.Database.GetMigrations().ToList();
        Assert.NotEmpty(migrations);
        var firstMigration = migrations[0];
        var migrator = db.GetService<IMigrator>();

        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        db.Projects.Add(new Project
        {
            Name = "Survives Down/Up",
            Description = "Present while migrations re-run",
            OrganizationId = await SeededOrganizationIdAsync(db),
        });
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        if (migrations.Count > 1)
        {
            // Roll back to the first migration (keeps the Projects table & its row).
            var downException = await Record.ExceptionAsync(
                () => migrator.MigrateAsync(firstMigration, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Null(downException);
        }

        // Re-apply every migration on top of the pre-existing row.
        var upException = await Record.ExceptionAsync(() => migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(upException);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await db.Projects.AnyAsync(p => p.Name == "Survives Down/Up", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddAlertRuleProvisioningKey_WithDuplicateLegacyCandidates_AssignsOnlyOneManagedKey()
    {
        await ResetDatabaseAsync();
        await using var db = new AppDbContext(BuildOptions());
        var migrations = db.Database.GetMigrations().ToList();
        const string targetMigration = "20260717143532_AddAlertRuleProvisioningKey";
        var targetIndex = migrations.IndexOf(targetMigration);
        Assert.True(targetIndex > 0, $"Could not locate migration {targetMigration} after a predecessor.");

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[targetIndex - 1], TestContext.Current.CancellationToken);

        var organizationId = await SeededOrganizationIdAsync(db);
        int serverId;
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            // This test deliberately parks the schema on an old migration. Insert only columns that
            // existed then; using the current EF model would reference future columns by construction.
            command.CommandText =
                """
                INSERT INTO "Servers"
                    ("Name", "Hostname", "OsDescription", "IpAddress", "AgentVersion", "Status", "Type",
                     "LastHeartbeat", "Tags", "CreatedAt", "UpdatedAt", "OrganizationId")
                VALUES
                    ('Legacy storage server', 'legacy-storage', 'Linux', '127.0.0.1', '1.0.0', 0, 0,
                     NOW(), '[]', NOW(), NOW(), @organizationId)
                RETURNING "Id";
                """;
            command.Parameters.AddWithValue("organizationId", organizationId);
            serverId = Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "AlertRules"
                    ("Name", "ServerId", "Metric", "Operator", "Threshold", "SustainedSeconds",
                     "Severity", "IsEnabled", "CreatedAt", "UpdatedAt")
                VALUES
                    ('Stockage historique A', @serverId, 2, 2, 80, 300, 1, TRUE, NOW(), NOW()),
                    ('Stockage historique B', @serverId, 2, 2, 90, 300, 2, TRUE, NOW(), NOW());
                """;
            command.Parameters.AddWithValue("serverId", serverId);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var exception = await Record.ExceptionAsync(() =>
            migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(exception);
        var keys = await db.AlertRules.AsNoTracking()
            .Where(rule => rule.Name == "Stockage historique A" || rule.Name == "Stockage historique B")
            .OrderBy(rule => rule.Id)
            .Select(rule => rule.ProvisioningKey)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Collection(keys,
            key => Assert.Equal($"storage-disk-warning:{serverId}", key),
            key => Assert.Null(key));
    }

    [Fact]
    public async Task AddConfigurableQualityGates_BackfillsStableKeys_AndEnforcesScopeUniqueness()
    {
        await ResetDatabaseAsync();
        await using var db = new AppDbContext(BuildOptions());
        var migrations = db.Database.GetMigrations().ToList();
        const string targetMigration = "20260726152803_AddConfigurableQualityGates";
        var targetIndex = migrations.IndexOf(targetMigration);
        Assert.True(targetIndex > 0, $"Could not locate migration {targetMigration} after a predecessor.");

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[targetIndex - 1], TestContext.Current.CancellationToken);
        int legacyId;
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO "AnalysisPolicies"
                    ("Name", "MetricKey", "Operator", "Threshold", "Behavior", "Priority",
                     "Enabled", "Version", "CreatedAt", "UpdatedAt")
                VALUES
                    ('Legacy coverage', 'coverage.line.percent', 2, 75, 1, 0,
                     TRUE, 4, NOW(), NOW())
                RETURNING "Id";
                """;
            legacyId = Convert.ToInt32(
                await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        await migrator.MigrateAsync(targetMigration, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var legacy = await db.AnalysisPolicies.AsNoTracking()
            .SingleAsync(item => item.Id == legacyId, TestContext.Current.CancellationToken);
        Assert.Equal($"legacy.{legacyId}", legacy.PolicyKey);
        Assert.False(legacy.NewFindingsOnly);
        Assert.Equal(4, legacy.Version);

        db.AnalysisPolicies.Add(new AnalysisPolicy
        {
            PolicyKey = legacy.PolicyKey,
            Name = "Duplicate key",
            MetricKey = "coverage.line.percent",
            Operator = AnalysisPolicyOperator.LessThan,
            Threshold = 80,
            Behavior = AnalysisGateBehavior.Block,
            Enabled = true,
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("IX_AnalysisPolicies_OrganizationId_ProjectId_PolicyKey", postgres.ConstraintName);
    }

    [Fact]
    public async Task EnforceSingleDeployedRelease_NormalizesLegacyDuplicates_AndRejectsAnotherActiveRelease()
    {
        await ResetDatabaseAsync();
        await using var db = new AppDbContext(BuildOptions());
        var migrations = db.Database.GetMigrations().ToList();
        const string targetMigration = "20260720082341_EnforceSingleDeployedReleasePerProject";
        var targetIndex = migrations.IndexOf(targetMigration);
        Assert.True(targetIndex > 0, $"Could not locate migration {targetMigration} after a predecessor.");

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[targetIndex - 1], TestContext.Current.CancellationToken);

        var projectId = await LegacyProjectRows.InsertAsync(
            db, "Release invariant", await SeededOrganizationIdAsync(db), TestContext.Current.CancellationToken,
            "Migration regression fixture");

        // Releases, unlike Projects, is written in SQL rather than through the entity. The database
        // sits at a 2026-07 migration while Release describes today's schema, so an EF insert or
        // query emits every column added since - AssuranceGrade among them - and PostgreSQL answers
        // 42703 for a column this migration point does not have yet. Naming the columns that existed
        // then is what makes this test about the migration instead of about the current model, and
        // keeps it from breaking again the next time a release column is added.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Releases" ("ProjectId", "Version", "BranchName", "Status", "DetectedAt", "PublishedAt")
            VALUES
              (@projectId, '1.0.0', 'main', @deployed, @olderAt, @olderAt),
              (@projectId, '1.1.0', 'main', @deployed, @newerAt, @newerAt);
            """,
            [
                new NpgsqlParameter("projectId", projectId),
                new NpgsqlParameter("deployed", (int)ReleaseStatus.Deployed),
                new NpgsqlParameter("olderAt", new DateTime(2026, 7, 19, 8, 0, 0, DateTimeKind.Utc)),
                new NpgsqlParameter("newerAt", new DateTime(2026, 7, 20, 8, 0, 0, DateTimeKind.Utc))
            ],
            TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(targetMigration, TestContext.Current.CancellationToken);

        // The migration must demote every deployed release but the most recent one.
        var olderStatus = await ScalarAsync(db,
            """SELECT "Status" FROM "Releases" WHERE "Version" = '1.0.0';""");
        var newerStatus = await ScalarAsync(db,
            """SELECT "Status" FROM "Releases" WHERE "Version" = '1.1.0';""");
        Assert.Equal((int)ReleaseStatus.Published, olderStatus);
        Assert.Equal((int)ReleaseStatus.Deployed, newerStatus);

        // And the index it creates must refuse a second deployed release from then on.
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Releases" ("ProjectId", "Version", "BranchName", "Status", "DetectedAt", "PublishedAt")
                VALUES (@projectId, '1.2.0', 'main', @deployed, now(), now());
                """,
                [
                    new NpgsqlParameter("projectId", projectId),
                    new NpgsqlParameter("deployed", (int)ReleaseStatus.Deployed)
                ],
                TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("IX_Releases_ProjectId", exception.ConstraintName);
    }

    /// <summary>
    /// Drops every object in the public schema so each test starts from a pristine database
    /// (the container is shared across the test run for speed).
    /// </summary>
    private async Task ResetDatabaseAsync()
    {
        await using var db = new AppDbContext(BuildOptions());
        await db.Database.ExecuteSqlRawAsync(
            "DROP SCHEMA public CASCADE; CREATE SCHEMA public;");
    }
}
