// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.IntegrationTests;

/// <summary>The batch average duration runs in one PostgreSQL query over the last ten successful runs.</summary>
public sealed class PipelineAverageDurationIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    private static readonly DateTime Origin = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetAverageMinutesAsync_BatchOfPipelines_AnswersEachOneFromOneQuery()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        int neverRan, three, many, failedOnly, mixed;
        await using (var seed = NewContext())
        {
            var organization = new Organization { Name = $"org-{Guid.NewGuid():N}", Slug = $"s{Guid.NewGuid():N}"[..12] };
            seed.Organizations.Add(organization);
            await seed.SaveChangesAsync(ct);
            var project = new Project { Name = "Average", OrganizationId = organization.Id };
            seed.Projects.Add(project);
            await seed.SaveChangesAsync(ct);
            var pipelines = new[] { "never", "three", "many", "failed", "mixed" }
                .Select(name => new Pipeline { ProjectId = project.Id, Name = name, YamlDefinition = $"name: {name}\nstages: []" })
                .ToArray();
            seed.Pipelines.AddRange(pipelines);
            await seed.SaveChangesAsync(ct);
            (neverRan, three, many, failedOnly, mixed) = (pipelines[0].Id, pipelines[1].Id, pipelines[2].Id, pipelines[3].Id, pipelines[4].Id);

            // Three runs of 10, 20 and 60 minutes: average 30.
            AddRuns(seed, three, PipelineStatus.Success, [10, 20, 60], firstDay: 1);
            // Twelve runs: the two oldest (500 minutes) fall out of the sample, the ten latest last 20 minutes.
            AddRuns(seed, many, PipelineStatus.Success, [500, 500, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20], firstDay: 1);
            AddRuns(seed, failedOnly, PipelineStatus.Failed, [90, 90], firstDay: 1);
            // Two successes of 40 minutes, a failure and a cancellation of 600 minutes that must be ignored.
            AddRuns(seed, mixed, PipelineStatus.Success, [40, 40], firstDay: 1);
            AddRuns(seed, mixed, PipelineStatus.Failed, [600], firstDay: 10);
            AddRuns(seed, mixed, PipelineStatus.Cancelled, [600], firstDay: 11);
            await seed.SaveChangesAsync(ct);
        }

        await using var db = NewContext();
        using var counter = new SqlCommandCounter();
        var measure = await counter.MeasureAsync(() =>
            new PipelineScheduleRepository(db).GetAverageMinutesAsync([neverRan, three, many, failedOnly, mixed], ct));

        Assert.Equal(1, measure.Commands);
        Assert.Equal(3, measure.Result.Count);
        Assert.False(measure.Result.ContainsKey(neverRan));
        Assert.False(measure.Result.ContainsKey(failedOnly));
        Assert.Equal(30, measure.Result[three]);
        Assert.Equal(20, measure.Result[many]);
        Assert.Equal(40, measure.Result[mixed]);
    }

    private static void AddRuns(AppDbContext db, int pipelineId, PipelineStatus status, int[] minutes, int firstDay)
    {
        for (var i = 0; i < minutes.Length; i++)
        {
            var start = Origin.AddDays(firstDay + i);
            db.PipelineRuns.Add(new PipelineRun
            {
                PipelineId = pipelineId,
                Status = status,
                StartedAt = start,
                CompletedAt = start.AddMinutes(minutes[i])
            });
        }
    }
}
