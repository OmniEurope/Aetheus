// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests;

/// <summary>Recette R-22: retrying the failed steps of a run whose stage confirmation timed out drops the
/// warning of that stage, and only that one.</summary>
public sealed class PipelineRetryConfirmationWarningTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly PipelineRepository _repo;

    public PipelineRetryConfirmationWarningTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new PipelineRepository(
            _db,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(_db, TimeProvider.System),
            new PipelineRunLineageRepository(_db));
    }

    public void Dispose() => _db.Dispose();

    private static string Confirmation(string code, string stage) =>
        PipelineProblem.Of(code, $"Stage '{stage}' was not confirmed.", ("stage", stage)).ToStored();

    private async Task<PipelineRun> FailedRunAsync(string[] warnings, params string[] failedStages)
    {
        var pipeline = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var run = new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Failed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            WarningsJson = JsonSerializer.Serialize(warnings)
        };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var order = 0;
        _db.PipelineStepRuns.Add(new PipelineStepRun
        {
            PipelineRunId = run.Id,
            StageName = "Build",
            StepName = "ok",
            Order = order++,
            Status = TaskExecutionStatus.Success
        });
        foreach (var stage in failedStages)
            _db.PipelineStepRuns.Add(new PipelineStepRun
            {
                PipelineRunId = run.Id,
                StageName = stage,
                StepName = "step",
                Order = order++,
                Status = TaskExecutionStatus.Failed,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return run;
    }

    private async Task<List<string>> WarningsOfAsync(int runId)
    {
        _db.ChangeTracker.Clear();
        var reloaded = await _db.PipelineRuns.FindAsync([runId], TestContext.Current.CancellationToken);
        Assert.NotNull(reloaded);
        return string.IsNullOrEmpty(reloaded.WarningsJson) ? [] : JsonSerializer.Deserialize<List<string>>(reloaded.WarningsJson)!;
    }

    [Fact]
    public async Task ResetFailedStepRunsAsync_DropsTheTimedOutConfirmationWarningOfTheReplayedStage()
    {
        var run = await FailedRunAsync([Confirmation(PipelineProblemCodes.StageConfirmationTimedOut, "Confirm")], "Confirm");

        var count = await _repo.ResetFailedStepRunsAsync(run.Id, TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
        Assert.Empty(await WarningsOfAsync(run.Id));
    }

    [Fact]
    public async Task ResetFailedStepRunsAsync_KeepsEveryOtherWarning()
    {
        var other = PipelineProblem.Of(PipelineProblemCodes.AiStepFallback, "Fallback used.").ToStored();
        var elsewhere = Confirmation(PipelineProblemCodes.StageConfirmationTimedOut, "Release");
        var run = await FailedRunAsync(
            [Confirmation(PipelineProblemCodes.StageConfirmationRejected, "Confirm"), other, elsewhere, "Plain warning"], "Confirm");

        await _repo.ResetFailedStepRunsAsync(run.Id, TestContext.Current.CancellationToken);

        Assert.Equal([other, elsewhere, "Plain warning"], await WarningsOfAsync(run.Id));
    }
}
