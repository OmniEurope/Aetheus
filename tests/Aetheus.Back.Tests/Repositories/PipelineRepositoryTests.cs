// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Repositories;

public class PipelineRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly PipelineRepository _repo;

    public PipelineRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new PipelineRepository(
            _db,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Aetheus.Back.Components.Pipelines.PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(_db, TimeProvider.System),
            new PipelineRunLineageRepository(_db));
    }

    public void Dispose() => _db.Dispose();

    // --- GetPipelinesPagedAsync ---

    [Fact]
    public async Task GetPipelinesPagedAsync_ReturnsEmpty_WhenNoPipelines()
    {
        var (items, count) = await _repo.GetPipelinesPagedAsync(null, null, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Empty(items);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task GetActiveRunsAsync_ReturnsOnlyAccessibleActiveRuns()
    {
        var allowed = new Pipeline { Name = "Allowed" };
        var denied = new Pipeline { Name = "Denied" };
        _db.Pipelines.AddRange(allowed, denied);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineRuns.AddRange(
            new PipelineRun { PipelineId = allowed.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow },
            new PipelineRun { PipelineId = allowed.Id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow.AddMinutes(-1) },
            new PipelineRun { PipelineId = denied.Id, Status = PipelineStatus.Pending, StartedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runs = await _repo.GetActiveRunsAsync([allowed.Id], ct: TestContext.Current.CancellationToken);

        var run = Assert.Single(runs);
        Assert.Equal("Allowed", run.PipelineName);
        Assert.Equal(PipelineStatus.Running, run.Status);
    }

    [Fact]
    public async Task IsStepRetryEligibleAsync_TransientScannerDownloadFailure_IsRetryable()
    {
        var pipeline = new Pipeline { Name = "Security" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var step = new PipelineStepRun
        {
            PipelineRunId = run.Id,
            StageName = "SAST",
            StepName = "OpenGrep"
        };
        _db.PipelineStepRuns.Add(step);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.Tasks.Add(new ServerTask
        {
            ServerId = 1,
            Name = "OpenGrep",
            Command = "opengrep",
            Operation = OperationKind.PipelineRunScanner,
            PipelineRunId = run.Id,
            PipelineStepRunId = step.Id
        });
        _db.AnalysisReports.Add(new AnalysisReport
        {
            PipelineRunId = run.Id,
            ScannerKey = "opengrep",
            ScannerName = "OpenGrep",
            ScannerVersion = "1.22.0",
            Status = AnalysisReportStatus.Unavailable,
            ErrorMessage = "Scanner runtime download failed because of a transient network error."
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var eligible = await _repo.IsStepRetryEligibleAsync(
            run.Id,
            step.Id,
            TestContext.Current.CancellationToken);

        Assert.True(eligible);
    }

    [Fact]
    public async Task GetRecentRunsAsync_ReturnsAllStatusesNewestFirstWithinAccessScope()
    {
        var allowed = new Pipeline { Name = "Allowed" };
        var denied = new Pipeline { Name = "Denied" };
        _db.Pipelines.AddRange(allowed, denied);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var older = new PipelineRun
        {
            PipelineId = allowed.Id,
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-2)
        };
        var newest = new PipelineRun
        {
            PipelineId = allowed.Id,
            Status = PipelineStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        _db.PipelineRuns.AddRange(older, newest, new PipelineRun
        {
            PipelineId = denied.Id,
            Status = PipelineStatus.Failed,
            StartedAt = DateTime.UtcNow.AddMinutes(1)
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runs = await _repo.GetRecentRunsAsync([allowed.Id], ct: TestContext.Current.CancellationToken);

        Assert.Equal([newest.Id, older.Id], runs.Select(run => run.Id));
        Assert.Contains(runs, run => run.Status == PipelineStatus.Success);
    }

    [Fact]
    public async Task GetRecentRunsAsync_ReturnsOnlyTwentyNewestRuns()
    {
        var pipeline = new Pipeline { Name = "Limited" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var origin = DateTime.UtcNow.AddHours(-1);
        _db.PipelineRuns.AddRange(Enumerable.Range(1, 25).Select(index => new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Success,
            StartedAt = origin.AddMinutes(index)
        }));
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runs = await _repo.GetRecentRunsAsync([pipeline.Id], ct: TestContext.Current.CancellationToken);

        Assert.Equal(20, runs.Count);
        Assert.Equal(origin.AddMinutes(25), runs[0].StartedAt);
        Assert.Equal(origin.AddMinutes(6), runs[^1].StartedAt);
    }

    [Fact]
    public async Task GetPipelinesPagedAsync_ReturnsPaged_OrderedByName()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Name = "Zeta" },
            new Pipeline { Name = "Alpha" },
            new Pipeline { Name = "Beta" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, count) = await _repo.GetPipelinesPagedAsync(null, null, 1, 2, ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, count);
        Assert.Equal(2, items.Count);
        Assert.Equal("Alpha", items[0].Name);
        Assert.Equal("Beta", items[1].Name);
    }

    [Fact]
    public async Task R2093_PagedLists_PutPipelinesInProgressFirst_MostRecentlyStartedFirst_ThenByName()
    {
        // Recette R2-093: a running pipeline stayed at its alphabetical place.
        var at = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var alpha = new Pipeline { Name = "Alpha" };
        var beta = new Pipeline { Name = "Beta" };
        var yankee = new Pipeline { Name = "Yankee" };
        var zulu = new Pipeline { Name = "Zulu" };
        _db.Pipelines.AddRange(alpha, beta, yankee, zulu);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineRuns.AddRange(
            new PipelineRun { PipelineId = alpha.Id, Status = PipelineStatus.Success, StartedAt = at.AddMinutes(30) },
            new PipelineRun { PipelineId = yankee.Id, Status = PipelineStatus.WaitingForApproval, StartedAt = at },
            new PipelineRun { PipelineId = zulu.Id, Status = PipelineStatus.Running, StartedAt = at.AddMinutes(10) },
            new PipelineRun { PipelineId = zulu.Id, Status = PipelineStatus.Failed, StartedAt = at.AddMinutes(-10) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (entities, _) = await _repo.GetPipelinesPagedAsync(null, null, 1, 10, ct: TestContext.Current.CancellationToken);
        var (projected, _) = await _repo.GetPipelinesPagedProjectedAsync(
            null, null, null, null, null, 1, 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["Zulu", "Yankee", "Alpha", "Beta"], entities.Select(pipeline => pipeline.Name));
        Assert.Equal(["Zulu", "Yankee", "Alpha", "Beta"], projected.Select(pipeline => pipeline.Name));
    }

    [Fact]
    public async Task R2103_ACancellation_KeepsItsFirstAuthorAndReason_OnTheRunAndItsDto()
    {
        var pipeline = new Pipeline { Name = "release-fast" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.RequestPipelineRunCancellationAsync(
            run.Id, PipelineRunCancellation.ByUser("alice"), TestContext.Current.CancellationToken);
        // The stalled-cancellation recovery re-applies it as the system: the author stays alice.
        await _repo.RequestPipelineRunCancellationAsync(
            run.Id, PipelineRunCancellation.BySystem("re-applied"), TestContext.Current.CancellationToken);

        var dto = PipelineRunDtoMapper.MapRunToDto(await _db.PipelineRuns.SingleAsync(TestContext.Current.CancellationToken));
        Assert.True(dto.CancellationRequested);
        Assert.Equal("alice", dto.CancelledBy);
        Assert.Equal("Cancelled by a user", dto.CancellationReason);
    }

    [Fact]
    public async Task GetPipelinesPagedAsync_FiltersById_WhenAccessibleIdsProvided()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Id = 1, Name = "A" },
            new Pipeline { Id = 2, Name = "B" },
            new Pipeline { Id = 3, Name = "C" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, count) = await _repo.GetPipelinesPagedAsync(null, null, 1, 10, [1, 3], ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, count);
        Assert.All(items, i => Assert.Contains(i.Id, new[] { 1, 3 }));
    }

    [Fact]
    public async Task GetPipelinesPagedAsync_FiltersBySearch()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Name = "Deploy" },
            new Pipeline { Name = "Build" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, count) = await _repo.GetPipelinesPagedAsync("Deploy", null, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, count);
        Assert.Equal("Deploy", items[0].Name);
    }

    [Fact]
    public async Task GetPipelinesPagedAsync_FiltersByTriggerType()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Name = "A", TriggerType = PipelineTriggerType.Manual },
            new Pipeline { Name = "B", TriggerType = PipelineTriggerType.Webhook });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, count) = await _repo.GetPipelinesPagedAsync(null, PipelineTriggerType.Webhook, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, count);
        Assert.Equal("B", items[0].Name);
    }

    // --- GetPipelineWithRunsAsync ---

    [Fact]
    public async Task GetPipelineWithRunsAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetPipelineWithRunsAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetPipelineWithRunsAsync_ReturnsPipelineWithRuns()
    {
        var pipeline = new Pipeline { Name = "Test" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineRuns.Add(new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetPipelineWithRunsAsync(pipeline.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Single(result.Runs);
    }

    // --- FindPipelineAsync ---

    [Fact]
    public async Task FindPipelineAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.FindPipelineAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task FindPipelineAsync_ReturnsPipeline_WhenFound()
    {
        var pipeline = new Pipeline { Name = "Found" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindPipelineAsync(pipeline.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("Found", result.Name);
    }

    // --- AddPipelineAsync / RemovePipelineAsync ---

    [Fact]
    public async Task AddPipelineAsync_PersistsPipeline()
    {
        await _repo.AddPipelineAsync(new Pipeline { Name = "New" }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.Pipelines.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemovePipelineAsync_DeletesPipeline()
    {
        var pipeline = new Pipeline { Name = "ToDelete" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.RemovePipelineAsync(pipeline, ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, await _db.Pipelines.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- Pipeline Run operations ---

    [Fact]
    public async Task AddPipelineRunAsync_PersistsRun()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.AddPipelineRunAsync(new PipelineRun { PipelineId = pipeline.Id }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.PipelineRuns.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetRunsAsync_ReturnsOrderedByStartedAtDesc()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineRuns.AddRange(
            new PipelineRun { PipelineId = pipeline.Id, StartedAt = DateTime.UtcNow.AddHours(-2) },
            new PipelineRun { PipelineId = pipeline.Id, StartedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runs = await _repo.GetRunsAsync(pipeline.Id, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, runs.Count);
        Assert.True(runs[0].StartedAt >= runs[1].StartedAt);
    }

    [Fact]
    public async Task GetRunDetailAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetRunDetailAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetRunDetailAsync_ReturnsRunWithStepRuns()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = run.Id, StageName = "build", StepName = "step1", Order = 1 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetRunDetailAsync(run.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Single(result.StepRuns);
    }

    [Fact]
    public async Task GetPipelineRunWithPipelineAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetPipelineRunWithPipelineAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    // --- Step Run operations ---

    [Fact]
    public async Task GetPendingStepRunsAsync_ReturnsOnlyPending_OrderedByOrder()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "s", StepName = "b", Order = 2, Status = TaskExecutionStatus.Pending },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "s", StepName = "a", Order = 1, Status = TaskExecutionStatus.Pending },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "s", StepName = "c", Order = 3, Status = TaskExecutionStatus.Success });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetPendingStepRunsAsync(run.Id, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        Assert.Equal("a", result[0].StepName);
        Assert.Equal("b", result[1].StepName);
    }

    // --- Stage completion checks ---

    [Fact]
    public async Task AreAllStepsInStageCompletedAsync_ReturnsFalse_WhenPendingSteps()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = run.Id, StageName = "build", StepName = "s1", Order = 1, Status = TaskExecutionStatus.Pending });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(await _repo.AreAllStepsInStageCompletedAsync(run.Id, "build", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AreAllStepsInStageCompletedAsync_ReturnsTrue_WhenAllComplete()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "build", StepName = "s1", Order = 1, Status = TaskExecutionStatus.Success },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "build", StepName = "s2", Order = 2, Status = TaskExecutionStatus.Failed });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await _repo.AreAllStepsInStageCompletedAsync(run.Id, "build", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HasAnyStepFailedInStageAsync_ReturnsTrue_WhenFailed()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = run.Id, StageName = "build", StepName = "s1", Order = 1, Status = TaskExecutionStatus.Failed });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await _repo.HasAnyStepFailedInStageAsync(run.Id, "build", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HasAnyStepFailedInStageAsync_ReturnsFalse_WhenNoneFailed()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = run.Id, StageName = "build", StepName = "s1", Order = 1, Status = TaskExecutionStatus.Success });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(await _repo.HasAnyStepFailedInStageAsync(run.Id, "build", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetCompletedStageNamesAsync_ReturnsOnlyFullySucceededStages()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "build", StepName = "s1", Order = 1, Status = TaskExecutionStatus.Success },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "build", StepName = "s2", Order = 2, Status = TaskExecutionStatus.Success },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "deploy", StepName = "s3", Order = 3, Status = TaskExecutionStatus.Failed });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var completed = await _repo.GetCompletedStageNamesAsync(run.Id, ct: TestContext.Current.CancellationToken);
        Assert.Single(completed);
        Assert.Contains("build", completed);
    }

    [Fact]
    public async Task GetTerminalStageNamesAsync_IncludesEveryTerminalStatusAndExcludesActiveStages()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "success", StepName = "s1", Order = 1, Status = TaskExecutionStatus.Success },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "failed", StepName = "s2", Order = 2, Status = TaskExecutionStatus.Failed },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "timeout", StepName = "s3", Order = 3, Status = TaskExecutionStatus.Timeout },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "cancelled", StepName = "s4", Order = 4, Status = TaskExecutionStatus.Cancelled },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "pending", StepName = "s5", Order = 5, Status = TaskExecutionStatus.Pending },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "assigned", StepName = "s6", Order = 6, Status = TaskExecutionStatus.Assigned },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "running", StepName = "s7", Order = 7, Status = TaskExecutionStatus.Running },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "mixed", StepName = "s8", Order = 8, Status = TaskExecutionStatus.Success },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "mixed", StepName = "s9", Order = 9, Status = TaskExecutionStatus.Running });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var settled = await _repo.GetTerminalStageNamesAsync(run.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(4, settled.Count);
        Assert.Contains("success", settled);
        Assert.Contains("failed", settled);
        Assert.Contains("timeout", settled);
        Assert.Contains("cancelled", settled);
        Assert.DoesNotContain("pending", settled);
        Assert.DoesNotContain("assigned", settled);
        Assert.DoesNotContain("running", settled);
        Assert.DoesNotContain("mixed", settled);
    }

    // --- UpdatePipelineRunStatusAsync ---

    [Fact]
    public async Task UpdatePipelineRunStatusAsync_UpdatesStatus_AndSetsCompletedAt()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.UpdatePipelineRunStatusAsync(run.Id, PipelineStatus.Success, ct: TestContext.Current.CancellationToken);

        var updated = await _db.PipelineRuns.FindAsync([run.Id], TestContext.Current.CancellationToken);
        Assert.Equal(PipelineStatus.Success, updated!.Status);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public async Task UpdatePipelineRunStatusAsync_FailedArtifactCollection_CannotBeRequalifiedAsSuccess()
    {
        var pipeline = new Pipeline { Name = "P" };
        var server = new Server { Name = "linux-01" };
        _db.Pipelines.Add(pipeline);
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.Add(new PipelineStepRun
        {
            PipelineRunId = run.Id,
            StageName = "ApplicationPayload",
            StepName = "Verify deploy payload completeness",
            Status = TaskExecutionStatus.Success
        });
        _db.Tasks.Add(new ServerTask
        {
            ServerId = server.Id,
            PipelineRunId = run.Id,
            Name = "Collect Artifacts (ApplicationPayload)",
            Command = "[]",
            Operation = OperationKind.PipelineCollectArtifacts,
            Status = TaskExecutionStatus.Failed
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.UpdatePipelineRunStatusAsync(
            run.Id,
            PipelineStatus.Failed,
            TestContext.Current.CancellationToken);

        var updated = await _db.PipelineRuns.FindAsync([run.Id], TestContext.Current.CancellationToken);
        Assert.Equal(PipelineStatus.Failed, updated!.Status);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public async Task UpdatePipelineRunStatusAsync_FailedArtifactCollection_TurnsRequestedSuccessIntoFailed()
    {
        // BACK-019: a run whose declared artifact was never collected cannot end Success.
        var pipeline = new Pipeline { Name = "P" };
        var server = new Server { Name = "linux-01" };
        _db.Pipelines.Add(pipeline);
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.Tasks.Add(new ServerTask
        {
            ServerId = server.Id,
            PipelineRunId = run.Id,
            Name = "Collect Artifacts (build)",
            Command = "[]",
            Operation = OperationKind.PipelineCollectArtifacts,
            Status = TaskExecutionStatus.Failed
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await _repo.HasFailedArtifactCollectionAsync(run.Id, TestContext.Current.CancellationToken));
        await _repo.UpdatePipelineRunStatusAsync(run.Id, PipelineStatus.Success, TestContext.Current.CancellationToken);

        var updated = await _db.PipelineRuns.FindAsync([run.Id], TestContext.Current.CancellationToken);
        Assert.Equal(PipelineStatus.Failed, updated!.Status);
    }

    [Fact]
    public async Task UpdatePipelineRunStatusAsync_DoesNothing_WhenRunNotFound()
    {
        await _repo.UpdatePipelineRunStatusAsync(999, PipelineStatus.Failed, ct: TestContext.Current.CancellationToken);
        // No exception
    }

    // --- Template operations ---

    [Fact]
    public async Task GetTemplatesAsync_ReturnsOrderedByCategoryThenName()
    {
        _db.PipelineTemplates.AddRange(
            new PipelineTemplate { Name = "B", Category = "Deploy" },
            new PipelineTemplate { Name = "A", Category = "Build" },
            new PipelineTemplate { Name = "C", Category = "Build" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var templates = await _repo.GetTemplatesAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, templates.Count);
        Assert.Equal("Build", templates[0].Category);
        Assert.Equal("A", templates[0].Name);
    }

    [Fact]
    public async Task GetTemplateSummariesAsync_AggregatesPipelinesAndLatestRunWithinOrganization()
    {
        var updatedAt = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        _db.PipelineTemplates.AddRange(
            new PipelineTemplate
            {
                Name = "shared-ci",
                Category = "CI",
                OrganizationId = 1,
                LatestVersion = 4,
                UpdatedAt = updatedAt
            },
            new PipelineTemplate
            {
                Name = "shared-ci",
                Category = "CI",
                OrganizationId = 2,
                LatestVersion = 1,
                UpdatedAt = updatedAt.AddDays(-1)
            });
        var organizationOneProject = new Project { Name = "First", OrganizationId = 1 };
        var organizationTwoProject = new Project { Name = "Second", OrganizationId = 2 };
        var usedOne = new Pipeline
        {
            Name = "Pipeline A",
            TemplateReferenceName = "shared-ci",
            Project = organizationOneProject
        };
        var usedTwo = new Pipeline
        {
            Name = "Pipeline B",
            TemplateReferenceName = "SHARED-CI",
            Project = organizationOneProject
        };
        var otherOrganization = new Pipeline
        {
            Name = "Pipeline C",
            TemplateReferenceName = "shared-ci",
            Project = organizationTwoProject
        };
        var unrelated = new Pipeline
        {
            Name = "Pipeline D",
            TemplateReferenceName = "another-template",
            Project = organizationOneProject
        };
        _db.Pipelines.AddRange(usedOne, usedTwo, otherOrganization, unrelated);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var persistedUpdatedAt = await _db.PipelineTemplates
            .Where(template => template.OrganizationId == 1)
            .Select(template => template.UpdatedAt)
            .SingleAsync(TestContext.Current.CancellationToken);

        var olderRun = new DateTime(2026, 8, 9, 8, 0, 0, DateTimeKind.Utc);
        var newestRun = new DateTime(2026, 8, 10, 11, 0, 0, DateTimeKind.Utc);
        _db.PipelineRuns.AddRange(
            new PipelineRun { PipelineId = usedOne.Id, StartedAt = olderRun },
            new PipelineRun { PipelineId = usedTwo.Id, StartedAt = newestRun },
            new PipelineRun { PipelineId = otherOrganization.Id, StartedAt = newestRun.AddHours(2) },
            new PipelineRun { PipelineId = unrelated.Id, StartedAt = newestRun.AddHours(3) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var summaries = await _repo.GetTemplateSummariesAsync(TestContext.Current.CancellationToken);

        var organizationOne = summaries.Single(summary => summary.OrganizationId == 1);
        Assert.Equal(4, organizationOne.Version);
        Assert.Equal(persistedUpdatedAt, organizationOne.UpdatedAt);
        Assert.Equal(2, organizationOne.PipelineCount);
        Assert.Equal(newestRun, organizationOne.LatestRunAt);

        var organizationTwo = summaries.Single(summary => summary.OrganizationId == 2);
        Assert.Equal(1, organizationTwo.PipelineCount);
        Assert.Equal(newestRun.AddHours(2), organizationTwo.LatestRunAt);
    }

    [Fact]
    public async Task GetTemplateAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetTemplateAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetTemplateAsync_ReturnsTemplate_WhenFound()
    {
        var template = new PipelineTemplate
        {
            Name = "T1",
            Category = "CI",
            Versions =
            [
                new PipelineTemplateVersion
                {
                    Version = 1,
                    YamlContent = "name: t1",
                    ChangelogEntry = "init"
                }
            ]
        };
        _db.PipelineTemplates.Add(template);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();

        var result = await _repo.GetTemplateAsync(template.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("T1", result.Name);
        Assert.Empty(result.Versions);
    }

    [Fact]
    public async Task GetTemplateVersionsPagedAsync_ReturnsMetadataPage()
    {
        var template = new PipelineTemplate { Name = "T1", Category = "CI", LatestVersion = 15 };
        template.Versions.AddRange(Enumerable.Range(1, 15).Select(version => new PipelineTemplateVersion
        {
            Version = version,
            YamlContent = $"name: yaml-{version}",
            ChangelogEntry = $"change-{version}",
            CreatedByUsername = "alice",
            CreatedAt = new DateTime(2026, 1, version, 0, 0, 0, DateTimeKind.Utc)
        }));
        _db.PipelineTemplates.Add(template);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetTemplateVersionsPagedAsync(
            template.Id, 2, 10, "Version", true, ct: TestContext.Current.CancellationToken);

        Assert.Equal(15, result.TotalCount);
        Assert.Equal(5, result.Items.Count);
        Assert.Equal(5, result.Items[0].Version);
        Assert.Equal("change-5", result.Items[0].ChangelogEntry);
    }

    [Fact]
    public async Task FindTemplateByNameAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.FindTemplateByNameAsync("nonexistent", ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task FindTemplateByNameAsync_ReturnsTemplate_WhenFound()
    {
        _db.PipelineTemplates.Add(new PipelineTemplate { Name = "deploy-prod", Category = "Deploy" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindTemplateByNameAsync("deploy-prod", ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task AddTemplateAsync_PersistsTemplate()
    {
        await _repo.AddTemplateAsync(new PipelineTemplate { Name = "New", Category = "CI" }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.PipelineTemplates.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveTemplateAsync_DeletesTemplate()
    {
        var template = new PipelineTemplate { Name = "ToDelete", Category = "CI" };
        _db.PipelineTemplates.Add(template);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.RemoveTemplateAsync(template, ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, await _db.PipelineTemplates.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- CancelActiveStepRunsAndTasksAsync ---

    [Fact]
    public async Task CancelActiveStepRunsAndTasksAsync_CancelsAllNonTerminalWork()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "s", StepName = "a", Order = 1, Status = TaskExecutionStatus.Pending },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "s", StepName = "b", Order = 2, Status = TaskExecutionStatus.Assigned },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "s", StepName = "c", Order = 3, Status = TaskExecutionStatus.Running },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "s", StepName = "d", Order = 4, Status = TaskExecutionStatus.Success });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.Tasks.AddRange(
            new ServerTask
            {
                ServerId = 1,
                Name = "pending",
                Command = "pending",
                PipelineRunId = run.Id,
                Status = TaskExecutionStatus.Pending,
                EnvironmentVariables = "encrypted-pending"
            },
            new ServerTask
            {
                ServerId = 1,
                Name = "assigned",
                Command = "assigned",
                PipelineRunId = run.Id,
                Status = TaskExecutionStatus.Assigned,
                EnvironmentVariables = "encrypted-assigned"
            },
            new ServerTask
            {
                ServerId = 1,
                Name = "running",
                Command = "running",
                PipelineRunId = run.Id,
                Status = TaskExecutionStatus.Running,
                EnvironmentVariables = "encrypted-running"
            },
            new ServerTask
            {
                ServerId = 1,
                Name = "success",
                Command = "success",
                PipelineRunId = run.Id,
                Status = TaskExecutionStatus.Success,
                EnvironmentVariables = "{}"
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.CancelActiveStepRunsAndTasksAsync(run.Id, ct: TestContext.Current.CancellationToken);

        var steps = await _db.PipelineStepRuns.Where(s => s.PipelineRunId == run.Id).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, steps.Count(s => s.Status == TaskExecutionStatus.Cancelled));
        Assert.Equal(1, steps.Count(s => s.Status == TaskExecutionStatus.Success));
        Assert.All(steps.Where(s => s.Status == TaskExecutionStatus.Cancelled), step => Assert.NotNull(step.CompletedAt));

        var tasks = await _db.Tasks.Where(t => t.PipelineRunId == run.Id).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, tasks.Count(t => t.Status == TaskExecutionStatus.Cancelled));
        Assert.Equal(1, tasks.Count(t => t.Status == TaskExecutionStatus.Success));
        Assert.All(tasks.Where(t => t.Status == TaskExecutionStatus.Cancelled), task =>
        {
            Assert.NotNull(task.CompletedAt);
            Assert.Equal("{}", task.EnvironmentVariables);
        });
    }

    [Fact]
    public async Task GetTaskQueuePositionsAsync_UsesLivePerRunnerOrder()
    {
        var createdAt = new DateTime(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);
        var first = new ServerTask
        {
            ServerId = 7,
            Name = "first",
            Command = "first",
            Status = TaskExecutionStatus.Pending,
            CreatedAt = createdAt
        };
        var second = new ServerTask
        {
            ServerId = 7,
            Name = "second",
            Command = "second",
            Status = TaskExecutionStatus.Assigned,
            CreatedAt = createdAt.AddSeconds(1)
        };
        var completed = new ServerTask
        {
            ServerId = 7,
            Name = "completed",
            Command = "completed",
            Status = TaskExecutionStatus.Success,
            CreatedAt = createdAt.AddSeconds(-1)
        };
        var otherRunner = new ServerTask
        {
            ServerId = 8,
            Name = "other",
            Command = "other",
            Status = TaskExecutionStatus.Pending,
            CreatedAt = createdAt.AddSeconds(-2)
        };
        _db.Tasks.AddRange(first, second, completed, otherRunner);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var positions = await _repo.GetTaskQueuePositionsAsync(
            [first.Id, second.Id, completed.Id, otherRunner.Id],
            TestContext.Current.CancellationToken);

        Assert.Equal(new TaskQueuePosition(1, 2), positions[first.Id]);
        Assert.Equal(new TaskQueuePosition(2, 2), positions[second.Id]);
        Assert.False(positions.ContainsKey(completed.Id));
        Assert.Equal(new TaskQueuePosition(1, 1), positions[otherRunner.Id]);
    }

    // --- Artifact operations ---

    [Fact]
    public async Task GetArtifactsAsync_ReturnsArtifactsForRun()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineArtifacts.AddRange(
            new PipelineArtifact { PipelineRunId = run.Id, Name = "art1", FilePath = "/a" },
            new PipelineArtifact { PipelineRunId = run.Id, Name = "art2", FilePath = "/b" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var artifacts = await _repo.GetArtifactsAsync(run.Id, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, artifacts.Count);
    }

    [Fact]
    public async Task AddArtifactAsync_PersistsArtifact()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.AddArtifactAsync(new PipelineArtifact { PipelineRunId = run.Id, Name = "art", FilePath = "/x" }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.PipelineArtifacts.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- Approval operations ---

    [Fact]
    public async Task GetApprovalsAsync_ReturnsApprovalsForRun()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var env = new Data.Entities.Environment { Name = "prod" };
        _db.Environments.Add(env);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineApprovals.Add(new PipelineApproval { PipelineRunId = run.Id, StageName = "deploy", EnvironmentId = env.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var approvals = await _repo.GetApprovalsAsync(run.Id, ct: TestContext.Current.CancellationToken);
        Assert.Single(approvals);
    }

    [Fact]
    public async Task FindApprovalAsync_ReturnsNull_WhenNotFound()
    {
        Assert.Null(await _repo.FindApprovalAsync(999, ct: TestContext.Current.CancellationToken));
    }

    // --- Test Results ---

    [Fact]
    public async Task GetTestResultsAsync_ReturnsOrderedResults()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.TestResults.AddRange(
            new TestResult { PipelineRunId = run.Id, TestName = "Z", TestSuite = "B", Outcome = TestOutcome.Passed },
            new TestResult { PipelineRunId = run.Id, TestName = "A", TestSuite = "A", Outcome = TestOutcome.Failed });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var results = await _repo.GetTestResultsAsync(run.Id, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0].TestSuite);
    }

    [Fact]
    public async Task AddTestResultsAsync_PersistsResults()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.AddTestResultsAsync([
            new TestResult { PipelineRunId = run.Id, TestName = "t1", Outcome = TestOutcome.Passed },
            new TestResult { PipelineRunId = run.Id, TestName = "t2", Outcome = TestOutcome.Failed }
        ], ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, await _db.TestResults.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- DeleteRunsOlderThanAsync ---

    [Fact]
    public async Task DeleteRunsOlderThanAsync_DeletesOldRuns_ReturnsCount()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineRuns.AddRange(
            new PipelineRun { PipelineId = pipeline.Id, StartedAt = DateTime.UtcNow.AddDays(-31) },
            new PipelineRun { PipelineId = pipeline.Id, StartedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var deleted = await _repo.DeleteRunsOlderThanAsync(DateTime.UtcNow.AddDays(-7), ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, deleted);
        Assert.Equal(1, await _db.PipelineRuns.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteRunsOlderThanAsync_Returns0_WhenNoOldRuns()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineRuns.Add(new PipelineRun { PipelineId = pipeline.Id, StartedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var deleted = await _repo.DeleteRunsOlderThanAsync(DateTime.UtcNow.AddDays(-7), ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task DeleteRunsOlderThanAsync_PreservesRunWithPendingDeferredCleanup()
    {
        var server = new Server { Name = "offline", Hostname = "offline", Status = ServerStatus.Offline };
        var pipeline = new Pipeline { Name = "P" };
        var run = new PipelineRun
        {
            Pipeline = pipeline,
            StartedAt = DateTime.UtcNow.AddDays(-31)
        };
        run.Tasks.Add(new ServerTask
        {
            Server = server,
            Name = "deferred cleanup",
            Command = "cleanup",
            Status = TaskExecutionStatus.Pending,
            IsDeferredCleanup = true,
            CreatedAt = DateTime.UtcNow.AddDays(-31)
        });
        _db.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var deleted = await _repo.DeleteRunsOlderThanAsync(
            DateTime.UtcNow.AddDays(-7),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, deleted);
        Assert.Equal(1, await _db.PipelineRuns.CountAsync(
            cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- Webhook / Scheduled queries ---

    [Fact]
    public async Task GetWebhookTriggeredPipelinesWithProjectAsync_ReturnsOnlyWebhookPipelines()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Name = "Manual", TriggerType = PipelineTriggerType.Manual },
            new Pipeline { Name = "Webhook", TriggerType = PipelineTriggerType.Webhook });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetWebhookTriggeredPipelinesWithProjectAsync(ct: TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal("Webhook", result[0].Name);
    }

    [Fact]
    public async Task GetScheduledPipelinesAsync_ReturnsOnlyScheduledPipelines()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Name = "Manual", TriggerType = PipelineTriggerType.Manual },
            new Pipeline { Name = "Scheduled", TriggerType = PipelineTriggerType.Schedule });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetScheduledPipelinesAsync(ct: TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal("Scheduled", result[0].Name);
    }

    [Fact]
    public async Task HasActiveRunAsync_ReturnsTrue_WhenRunning()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineRuns.Add(new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await _repo.HasActiveRunAsync(pipeline.Id, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HasActiveRunAsync_ReturnsFalse_WhenAllComplete()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineRuns.Add(new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(await _repo.HasActiveRunAsync(pipeline.Id, ct: TestContext.Current.CancellationToken));
    }

    // --- SaveChangesAsync ---

    [Fact]
    public async Task SaveChangesAsync_PersistsChanges()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _repo.SaveChangesAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.Pipelines.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- Environment checks ---

    [Fact]
    public async Task GetEnvironmentChecksAsync_ReturnsChecksForEnvironment()
    {
        var env = new Data.Entities.Environment { Name = "prod" };
        _db.Environments.Add(env);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.EnvironmentChecks.AddRange(
            new EnvironmentCheck { EnvironmentId = env.Id, Name = "Health", Type = EnvironmentCheckType.RestCallback },
            new EnvironmentCheck { EnvironmentId = env.Id, Name = "DNS", Type = EnvironmentCheckType.StatusCheck });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetEnvironmentChecksAsync(env.Id, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        Assert.Equal("DNS", result[0].Name);
    }

    // --- GetFailedStepRunsInStageAsync ---

    [Fact]
    public async Task GetFailedStepRunsInStageAsync_ReturnsOnlyFailedInStage()
    {
        var pipeline = new Pipeline { Name = "P" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "build", StepName = "s1", Order = 1, Status = TaskExecutionStatus.Failed },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "build", StepName = "s2", Order = 2, Status = TaskExecutionStatus.Success },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "deploy", StepName = "s3", Order = 3, Status = TaskExecutionStatus.Failed });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetFailedStepRunsInStageAsync(run.Id, "build", ct: TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal("s1", result[0].StepName);
    }

    // --- FindEnvironmentByNameAsync ---

    [Fact]
    public async Task FindEnvironmentByNameAsync_ReturnsNull_WhenNotFound()
    {
        Assert.Null(await _repo.FindEnvironmentByNameAsync("nonexistent", null, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindEnvironmentByNameAsync_ReturnsEnvironment_WhenFound()
    {
        _db.Environments.Add(new Data.Entities.Environment { Name = "staging" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindEnvironmentByNameAsync("staging", null, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("staging", result.Name);
    }

    [Fact]
    public async Task FindTemplateAsync_Found_ReturnsTemplate()
    {
        _db.PipelineTemplates.Add(new PipelineTemplate { Id = 1, Name = "tpl", YamlContent = "yaml" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindTemplateAsync(1, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("tpl", result!.Name);
    }

    [Fact]
    public async Task FindTemplateAsync_NotFound_ReturnsNull()
    {
        Assert.Null(await _repo.FindTemplateAsync(999, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindOnlineServerInPoolAsync_ReturnsOnlineServer()
    {
        var pool = new AgentPool { Name = "pool1" };
        _db.AgentPools.Add(pool);
        // Item #11: PipelineRunnerEnabled is secure-by-default OFF. Tests asserting that a
        // matching online server IS returned must explicitly opt the server in.
        var server = new Server
        {
            Name = "srv",
            Hostname = "h",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"pipeline.build\"]"
        };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.AgentPoolServers.Add(new AgentPoolServer { AgentPoolId = pool.Id, ServerId = server.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindOnlineServerInPoolAsync("pool1", ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FindOnlineServerInPoolAsync_OfflineServer_ReturnsNull()
    {
        var pool = new AgentPool { Name = "pool2" };
        _db.AgentPools.Add(pool);
        var server = new Server { Name = "srv", Hostname = "h", Status = ServerStatus.Offline };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.AgentPoolServers.Add(new AgentPoolServer { AgentPoolId = pool.Id, ServerId = server.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await _repo.FindOnlineServerInPoolAsync("pool2", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindOnlineServerInEnvironmentAsync_ReturnsOnlineServer()
    {
        var env = new Data.Entities.Environment { Name = "prod" };
        _db.Environments.Add(env);
        // Item #11: opt-in PipelineRunnerEnabled for the matching-server test case.
        var server = new Server
        {
            Name = "srv",
            Hostname = "h",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"pipeline.build\"]"
        };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = env.Id, ServerId = server.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindOnlineServerInEnvironmentAsync("prod", projectId: null, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task AddApprovalAsync_PersistsApproval()
    {
        var pipeline = new Pipeline { Name = "p", ProjectId = 1 };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.AddApprovalAsync(new PipelineApproval { PipelineRunId = run.Id, StageName = "deploy" }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.PipelineApprovals.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
}
