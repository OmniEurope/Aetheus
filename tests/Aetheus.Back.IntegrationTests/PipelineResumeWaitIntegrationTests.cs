// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Recette R-39: the wait for a resume on PostgreSQL, through the migration that adds
/// <c>ResumeDeadline</c>. The conditional updates are the concurrency checks: two resumes, or a resume
/// racing the expiry, move the run once.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PipelineResumeWaitIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    private static readonly DateTime Now = new(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PipelineRepository Repository(AppDbContext db) => new(
        db,
        TimeProvider.System,
        NullLogger<PipelineRepository>.Instance,
        new PipelineTaskLifecycleRepository(db, TimeProvider.System),
        new PipelineRunLineageRepository(db));

    [Fact]
    public async Task AParkedRun_WaitsWithItsDeadline_AndIsParkedOnce()
    {
        await ResetAndMigrateAsync();
        var runId = await SeedRolledBackRunAsync();

        await using var db = NewContext();
        var repo = Repository(db);

        Assert.True(await repo.TryParkRunAwaitingResumeAsync(runId, Now.AddHours(24), Ct));
        Assert.False(await repo.TryParkRunAwaitingResumeAsync(runId, Now.AddHours(48), Ct));

        await using var read = NewContext();
        var run = await read.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == runId, Ct);
        Assert.Equal(PipelineStatus.AwaitingResume, run.Status);
        Assert.Equal(Now.AddHours(24), run.ResumeDeadline);
        Assert.Null(run.CompletedAt);
    }

    [Fact]
    public async Task TwoResumesAtOnce_ReopenTheRunOnce_AndResetOnlyTheReplayedStages()
    {
        await ResetAndMigrateAsync();
        var runId = await SeedRolledBackRunAsync();
        await using (var park = NewContext())
            Assert.True(await Repository(park).TryParkRunAwaitingResumeAsync(runId, Now.AddHours(24), Ct));

        await using var first = NewContext();
        await using var second = NewContext();
        string[] replay = ["Up", "Confirm", "Rollback"];
        var outcomes = await Task.WhenAll(
            Repository(first).ReopenAwaitingRunAsync(runId, replay, Now, Ct),
            Repository(second).ReopenAwaitingRunAsync(runId, replay, Now, Ct));

        Assert.Equal(1, outcomes.Count(outcome => outcome == ResumeReopenOutcome.Reopened));
        Assert.Equal(1, outcomes.Count(outcome => outcome == ResumeReopenOutcome.NotWaiting));
        await using var read = NewContext();
        var run = await read.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == runId, Ct);
        Assert.Equal(PipelineStatus.Running, run.Status);
        // One window per run: the resume keeps the deadline of the first missed confirmation.
        Assert.Equal(Now.AddHours(24), run.ResumeDeadline);
        var steps = await read.PipelineStepRuns.AsNoTracking().Where(s => s.PipelineRunId == runId).ToListAsync(Ct);
        Assert.All(steps.Where(s => replay.Contains(s.StageName)), step =>
        {
            Assert.Equal(TaskExecutionStatus.Pending, step.Status);
            Assert.Null(step.CompletedAt);
            Assert.Null(step.FailureReason);
            Assert.Null(step.OutputVariablesJson);
        });
        var build = Assert.Single(steps, s => s.StageName == "Build");
        Assert.Equal(TaskExecutionStatus.Success, build.Status);
        Assert.Equal("""{"IMAGE":"aetheus:1.2.3"}""", build.OutputVariablesJson);
    }

    [Fact]
    public async Task AResumeAfterTheDeadline_ChangesNothing_AndTheSweepClosesTheRunOnce()
    {
        await ResetAndMigrateAsync();
        var runId = await SeedRolledBackRunAsync();
        await using (var park = NewContext())
            Assert.True(await Repository(park).TryParkRunAwaitingResumeAsync(runId, Now.AddHours(24), Ct));
        var later = Now.AddHours(25);

        await using var db = NewContext();
        var repo = Repository(db);
        Assert.Equal(ResumeReopenOutcome.Expired, await repo.ReopenAwaitingRunAsync(runId, ["Up"], later, Ct));
        Assert.Empty(await repo.GetExpiredResumeWaitRunIdsAsync(Now, Ct));
        Assert.Equal([runId], await repo.GetExpiredResumeWaitRunIdsAsync(later, Ct));
        Assert.False(await repo.TryCloseExpiredResumeWaitAsync(runId, Now, Ct));
        Assert.True(await repo.TryCloseExpiredResumeWaitAsync(runId, later, Ct));
        Assert.False(await repo.TryCloseExpiredResumeWaitAsync(runId, later, Ct));

        await using var read = NewContext();
        var run = await read.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == runId, Ct);
        Assert.Equal(PipelineStatus.Running, run.Status);
        // The deadline stays, in the past: it keeps the scheduler from parking the run again.
        Assert.Equal(Now.AddHours(24), run.ResumeDeadline);
        Assert.Equal(TaskExecutionStatus.Success,
            (await read.PipelineStepRuns.AsNoTracking().SingleAsync(s => s.PipelineRunId == runId && s.StageName == "Up", Ct)).Status);
        Assert.Empty(await repo.GetExpiredResumeWaitRunIdsAsync(later, Ct));
    }

    /// <summary>A resumed run that waits again is parked on the deadline it already had, and the expiry
    /// sweep ignores it while it is running, even after that deadline.</summary>
    [Fact]
    public async Task AResumedRun_ParkedAgain_KeepsItsFirstDeadline_AndTheSweepIgnoresItWhileRunning()
    {
        await ResetAndMigrateAsync();
        var runId = await SeedRolledBackRunAsync();
        var firstDeadline = Now.AddHours(24);

        await using var db = NewContext();
        var repo = Repository(db);
        Assert.True(await repo.TryParkRunAwaitingResumeAsync(runId, firstDeadline, Ct));
        Assert.Equal(ResumeReopenOutcome.Reopened, await repo.ReopenAwaitingRunAsync(runId, ["Up"], Now.AddHours(1), Ct));
        Assert.Empty(await repo.GetExpiredResumeWaitRunIdsAsync(Now.AddHours(30), Ct));
        Assert.True(await repo.TryParkRunAwaitingResumeAsync(runId, firstDeadline, Ct));

        await using var read = NewContext();
        var run = await read.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == runId, Ct);
        Assert.Equal(PipelineStatus.AwaitingResume, run.Status);
        Assert.Equal(firstDeadline, run.ResumeDeadline);
        Assert.Equal(ResumeReopenOutcome.Expired, await repo.ReopenAwaitingRunAsync(runId, ["Up"], Now.AddHours(25), Ct));
    }

    [Fact]
    public async Task TheBlockingFailures_AreReadByStage()
    {
        await ResetAndMigrateAsync();
        var runId = await SeedRolledBackRunAsync();

        await using var db = NewContext();

        Assert.Equal(["Confirm"], await Repository(db).GetBlockingFailedStageNamesAsync(runId, Ct));
    }

    /// <summary>A run as the engine leaves it once a missed confirmation was rolled back.</summary>
    private async Task<int> SeedRolledBackRunAsync()
    {
        await using var seed = NewContext();
        var organization = new Organization { Name = $"org-{Guid.NewGuid():N}", Slug = $"s{Guid.NewGuid():N}"[..12] };
        seed.Organizations.Add(organization);
        await seed.SaveChangesAsync(Ct);
        var project = new Project { Name = "Project", OrganizationId = organization.Id };
        seed.Projects.Add(project);
        await seed.SaveChangesAsync(Ct);
        var pipeline = new Pipeline { Name = "release", ProjectId = project.Id, YamlDefinition = "name: release\nstages: []" };
        seed.Pipelines.Add(pipeline);
        await seed.SaveChangesAsync(Ct);
        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running, AdditionalVariablesJson = "{}" };
        seed.PipelineRuns.Add(run);
        await seed.SaveChangesAsync(Ct);
        seed.PipelineStepRuns.AddRange(
            Step(run.Id, "Build", TaskExecutionStatus.Success, outputs: """{"IMAGE":"aetheus:1.2.3"}"""),
            Step(run.Id, "Up", TaskExecutionStatus.Success),
            Step(run.Id, "Confirm", TaskExecutionStatus.Failed, failure: "The confirmation window elapsed."),
            Step(run.Id, "Confirm", TaskExecutionStatus.Failed, failure: "The confirmation window elapsed."),
            Step(run.Id, "Rollback", TaskExecutionStatus.Success),
            new PipelineStepRun
            {
                PipelineRunId = run.Id,
                StageName = PipelineRunHelpers.SystemCleanupStage,
                StepName = "Cleanup",
                Status = TaskExecutionStatus.Pending,
                IsSystem = true
            });
        await seed.SaveChangesAsync(Ct);
        return run.Id;
    }

    private static PipelineStepRun Step(
        int runId, string stage, TaskExecutionStatus status, string? outputs = null, string? failure = null) => new()
        {
            PipelineRunId = runId,
            StageName = stage,
            StepName = stage.ToLowerInvariant(),
            Status = status,
            OutputVariablesJson = outputs,
            FailureReason = failure,
            StartedAt = Now.AddMinutes(-20),
            CompletedAt = Now.AddMinutes(-6)
        };
}
