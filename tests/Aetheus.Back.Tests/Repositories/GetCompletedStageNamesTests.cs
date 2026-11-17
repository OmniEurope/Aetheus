// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Repositories;

/// <summary>
/// Focused coverage for <see cref="PipelineRepository.GetStageProgressAsync"/>, especially the
/// ContinueOnError nuance: a stage whose only failure is a step marked ContinueOnError still counts
/// as completed, whereas a hard failure does not.
/// </summary>
public class GetCompletedStageNamesTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly PipelineRepository _repo;

    public GetCompletedStageNamesTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new PipelineRepository(
            _db,
            TimeProvider.System,
            NullLogger<PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(_db, TimeProvider.System),
            new PipelineRunLineageRepository(_db));
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedStepAsync(int runId, string stage, TaskExecutionStatus status, bool continueOnError = false)
    {
        _db.PipelineStepRuns.Add(new PipelineStepRun
        {
            PipelineRunId = runId,
            StageName = stage,
            StepName = $"{stage}-{Guid.NewGuid():N}",
            Status = status,
            ContinueOnError = continueOnError,
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task ContinueOnErrorFailure_CountsStageAsCompleted()
    {
        await SeedStepAsync(1, "Build", TaskExecutionStatus.Success);
        await SeedStepAsync(1, "Build", TaskExecutionStatus.Failed, continueOnError: true);

        var completed = (await _repo.GetStageProgressAsync(1, ct: TestContext.Current.CancellationToken)).Completed;

        Assert.Contains("Build", completed);
    }

    [Fact]
    public async Task HardFailure_DoesNotCountStageAsCompleted()
    {
        await SeedStepAsync(1, "Build", TaskExecutionStatus.Success);
        await SeedStepAsync(1, "Build", TaskExecutionStatus.Failed, continueOnError: false);

        var completed = (await _repo.GetStageProgressAsync(1, ct: TestContext.Current.CancellationToken)).Completed;

        Assert.DoesNotContain("Build", completed);
    }

    [Fact]
    public async Task RunningStep_DoesNotCountStageAsCompleted()
    {
        await SeedStepAsync(1, "Build", TaskExecutionStatus.Success);
        await SeedStepAsync(1, "Build", TaskExecutionStatus.Running);

        var completed = (await _repo.GetStageProgressAsync(1, ct: TestContext.Current.CancellationToken)).Completed;

        Assert.DoesNotContain("Build", completed);
    }

    [Fact]
    public async Task AllSuccessOrCancelled_CountsStageAsCompleted()
    {
        await SeedStepAsync(2, "Test", TaskExecutionStatus.Success);
        await SeedStepAsync(2, "Test", TaskExecutionStatus.Cancelled);

        var completed = (await _repo.GetStageProgressAsync(2, ct: TestContext.Current.CancellationToken)).Completed;

        Assert.Contains("Test", completed);
    }

    [Fact]
    public async Task HardFailure_CountsStageAsTerminal()
    {
        await SeedStepAsync(3, "E2E", TaskExecutionStatus.Success);
        await SeedStepAsync(3, "E2E", TaskExecutionStatus.Failed);

        var terminal = (await _repo.GetStageProgressAsync(3, ct: TestContext.Current.CancellationToken)).Terminal;

        Assert.Contains("E2E", terminal);
    }

    [Fact]
    public async Task OneRead_AnswersBothSets()
    {
        await SeedStepAsync(5, "Build", TaskExecutionStatus.Success);
        await SeedStepAsync(5, "Test", TaskExecutionStatus.Success);
        await SeedStepAsync(5, "Test", TaskExecutionStatus.Failed);
        await SeedStepAsync(5, "Deploy", TaskExecutionStatus.Running);

        var progress = await _repo.GetStageProgressAsync(5, ct: TestContext.Current.CancellationToken);

        // Test failed hard: settled, but not a successful dependency. Deploy is still running: neither.
        Assert.Equal(["Build"], progress.Completed);
        Assert.Equal(["Build", "Test"], progress.Terminal.Order());
    }

    [Fact]
    public async Task RunFailures_SplitBlockingFromContinuableInOneRead()
    {
        await SeedStepAsync(6, "Build", TaskExecutionStatus.Success);
        await SeedStepAsync(6, "Lint", TaskExecutionStatus.Failed, continueOnError: true);
        await SeedStepAsync(6, "Lint", TaskExecutionStatus.Timeout, continueOnError: true);
        await SeedStepAsync(6, "Test", TaskExecutionStatus.Timeout);

        var failures = await _repo.GetRunFailuresAsync(6, TestContext.Current.CancellationToken);

        Assert.True(failures.HasBlocking);
        Assert.Equal(2, failures.Continuable.Count);
        Assert.All(failures.Continuable, step => Assert.True(step.ContinueOnError));
        Assert.Equal(
            await _repo.HasAnyFailedStepInRunAsync(6, TestContext.Current.CancellationToken), failures.HasBlocking);
        Assert.Equal(
            (await _repo.GetContinuableFailedStepsInRunAsync(6, TestContext.Current.CancellationToken)).Select(step => step.Id).Order(),
            failures.Continuable.Select(step => step.Id).Order());
    }

    [Fact]
    public async Task RunFailures_OnlyContinuableFailures_AreNotBlocking()
    {
        await SeedStepAsync(7, "Build", TaskExecutionStatus.Success);
        await SeedStepAsync(7, "Lint", TaskExecutionStatus.Failed, continueOnError: true);

        var failures = await _repo.GetRunFailuresAsync(7, TestContext.Current.CancellationToken);

        Assert.False(failures.HasBlocking);
        Assert.Single(failures.Continuable);
    }

    [Fact]
    public async Task RunningStep_DoesNotCountStageAsTerminal()
    {
        await SeedStepAsync(4, "E2E", TaskExecutionStatus.Failed);
        await SeedStepAsync(4, "E2E", TaskExecutionStatus.Running);

        var terminal = (await _repo.GetStageProgressAsync(4, ct: TestContext.Current.CancellationToken)).Terminal;

        Assert.DoesNotContain("E2E", terminal);
    }
}
