// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The <c>AddProjectReleaseSeries</c> backfill, run for real on PostgreSQL: the project-wide counter
/// that every series used to share becomes the counter of series 1.3 (production is at 1.3.16), so 1.3
/// continues with 17 and every other series, 1.4 first, starts at 0. The runs that took a number are
/// stamped 1.3 and the numbers handed out are recorded, so none is given twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProjectReleaseSeriesMigrationTests(PostgresFixture fixture)
{
    private const string Migration = "20261019175513_AddProjectReleaseSeries";

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;

    [Fact]
    public async Task Up_TheSharedCounterBecomesTheCounterOfSeries13_AndANewSeriesStartsAtZero()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var reset = new AppDbContext(Options()))
            await reset.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct);

        int released, untouched, runId;
        await using (var db = new AppDbContext(Options()))
        {
            var migrations = db.Database.GetMigrations().ToList();
            var index = migrations.IndexOf(Migration);
            Assert.True(index > 0, $"Could not locate {Migration} after a predecessor.");
            await db.GetService<IMigrator>().MigrateAsync(migrations[index - 1], ct);

            var organizationId = await db.Organizations.OrderBy(o => o.Id).Select(o => o.Id).FirstAsync(ct);
            released = await LegacyProjectRows.InsertAsync(db, "Released-series", organizationId, ct);
            untouched = await LegacyProjectRows.InsertAsync(db, "Never-released-series", organizationId, ct);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"Projects\" SET \"ReleaseCounter\" = 17 WHERE \"Id\" = {0}", [released], ct);
            var pipelineId = await InsertAsync(db,
                """
                INSERT INTO "Pipelines" ("Name", "Description", "YamlDefinition", "TriggerType", "BuildCounter", "RowVersion", "CreatedAt", "UpdatedAt", "ProjectId")
                VALUES ('release', '', 'name: release', 0, 0, gen_random_uuid(), now(), now(), @project)
                RETURNING "Id" AS "Value"
                """, ct, new NpgsqlParameter("project", released));
            runId = await InsertAsync(db,
                """
                INSERT INTO "PipelineRuns" ("PipelineId", "Status", "StartedAt", "AdditionalVariablesJson", "ParametersJson", "BuildNumber", "RunToEnd", "ReleaseNumber")
                VALUES (@pipeline, 2, now(), '{{}}', '{{}}', 1, false, 16)
                RETURNING "Id" AS "Value"
                """, ct, new NpgsqlParameter("pipeline", pipelineId));
        }

        await using (var db = new AppDbContext(Options()))
            await db.Database.MigrateAsync(ct);

        await using (var db = new AppDbContext(Options()))
        {
            var repository = new ProjectReleaseNumberRepository(db, TimeProvider.System);

            Assert.Equal(16, await repository.GetLatestAsync(released, "1.3", ct));
            Assert.Equal(17, await repository.AllocateNextAsync(released, "1.3", ct));
            Assert.Equal(0, await repository.AllocateNextAsync(released, "1.4", ct));
            Assert.Equal(1, await repository.AllocateNextAsync(released, "1.4", ct));
            // A project whose code declares no VersionPrefix continues its counter too.
            Assert.Equal(17, await repository.AllocateNextAsync(released, ReleaseSeries.None, ct));
            Assert.Equal(SetNextReleaseNumberOutcome.NumberAlreadyUsed, await repository.SetNextAsync(released, "1.3", 16, ct));
            Assert.Equal(0, await repository.AllocateNextAsync(untouched, "1.3", ct));

            var run = await db.PipelineRuns.AsNoTracking().SingleAsync(item => item.Id == runId, ct);
            Assert.Equal("1.3", run.ReleaseSeries);
            Assert.Equal(new ReleaseNumberAssignment("1.3", 16), await repository.GetRunReleaseAsync(runId, ct));
            Assert.Equal(17, await db.ProjectReleaseNumberAllocations
                .CountAsync(item => item.ProjectId == released && item.Series == "1.3" && item.Number < 17, ct));
        }
    }

    private static async Task<int> InsertAsync(
        AppDbContext db, string sql, CancellationToken ct, params NpgsqlParameter[] parameters) =>
        (await db.Database.SqlQueryRaw<int>(sql, parameters).ToListAsync(ct)).Single();
}
