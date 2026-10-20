// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>The baseline query selects at most the requested number of successful same-pipeline
/// runs started before the viewed run.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RunStageBaselinesIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Baselines_SampleOnlyEarlierSuccessfulRunsOfThePipeline_BoundedByN()
    {
        await fixture.ResetAsync();
        var ct = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        var t0 = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        int pipelineId;
        int viewedRunId;
        await using (var seed = new AppDbContext(options))
        {
            var organization = new Organization { Name = $"org-{Guid.NewGuid():N}", Slug = $"s{Guid.NewGuid():N}"[..12] };
            seed.Organizations.Add(organization);
            await seed.SaveChangesAsync(ct);
            var project = new Project { Name = "Baselines", OrganizationId = organization.Id };
            seed.Projects.Add(project);
            await seed.SaveChangesAsync(ct);
            var pipeline = new Pipeline { ProjectId = project.Id, Name = "app-ci", YamlDefinition = "name: app-ci\nstages: []" };
            var other = new Pipeline { ProjectId = project.Id, Name = "other", YamlDefinition = "name: other\nstages: []" };
            seed.Pipelines.AddRange(pipeline, other);
            await seed.SaveChangesAsync(ct);
            pipelineId = pipeline.Id;

            void AddRun(Pipeline owner, PipelineStatus status, int hour, int buildSeconds)
            {
                var run = new PipelineRun { PipelineId = owner.Id, Status = status, StartedAt = t0.AddHours(hour) };
                seed.PipelineRuns.Add(run);
                seed.PipelineStepRuns.Add(new PipelineStepRun
                {
                    PipelineRun = run,
                    StageName = "Build",
                    StepName = "Build",
                    Status = TaskExecutionStatus.Success,
                    StartedAt = run.StartedAt,
                    CompletedAt = run.StartedAt.AddSeconds(buildSeconds)
                });
            }

            // Twelve successful runs: only the ten most recent may count (100..190 s, oldest two 10 s).
            AddRun(pipeline, PipelineStatus.Success, 0, 10);
            AddRun(pipeline, PipelineStatus.Success, 1, 10);
            for (var i = 0; i < 10; i++)
                AddRun(pipeline, PipelineStatus.Success, 2 + i, 100 + (i * 10));
            // Never sampled: a failed and a partial run of the pipeline, a success of another pipeline.
            AddRun(pipeline, PipelineStatus.Failed, 12, 5000);
            AddRun(pipeline, PipelineStatus.Partial, 13, 5000);
            AddRun(other, PipelineStatus.Success, 14, 5000);
            await seed.SaveChangesAsync(ct);

            var viewed = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running, StartedAt = t0.AddHours(15) };
            seed.PipelineRuns.Add(viewed);
            // A success that started AFTER the viewed run: not history for it.
            AddRun(pipeline, PipelineStatus.Success, 16, 9000);
            await seed.SaveChangesAsync(ct);
            viewedRunId = viewed.Id;
        }

        await using var db = new AppDbContext(options);
        var rows = await new PipelineCoverageRepository(db)
            .GetRecentSuccessfulStepTimingsAsync(pipelineId, viewedRunId, RunStageBaselineService.SampleSize, ct);
        var baselines = RunStageBaselineCalculator.Compute(rows);

        Assert.Equal(10, baselines.SampleRuns);
        var build = Assert.Single(baselines.Steps);
        Assert.Equal(145, build.AverageSeconds, precision: 3);
        Assert.Equal(190, build.LastSeconds, precision: 3);
        Assert.Equal(10, build.Samples);
        Assert.Equal(145, Assert.Single(baselines.Stages).AverageSeconds, precision: 3);
    }

    [Fact]
    public async Task Baselines_ForARunOfAnotherPipeline_AreEmpty()
    {
        await fixture.ResetAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        await using var db = new AppDbContext(options);

        var rows = await new PipelineCoverageRepository(db)
            .GetRecentSuccessfulStepTimingsAsync(pipelineId: 999_999, runId: 1, take: 10, TestContext.Current.CancellationToken);

        Assert.Empty(rows);
    }
}
