// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class PipelineRepositoryQueryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly PipelineRepository _repo;

    public PipelineRepositoryQueryTests()
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

    private void Step(int runId, TaskExecutionStatus status, bool system = false, bool continueOnError = false,
        string? outputs = null)
    {
        _db.PipelineStepRuns.Add(new PipelineStepRun
        {
            PipelineRunId = runId,
            Status = status,
            IsSystem = system,
            ContinueOnError = continueOnError,
            StageName = "Build",
            StepName = "compile",
            OutputVariablesJson = outputs
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task GetRunMetricsAsync_ReturnsMetricsForRunOrdered()
    {
        _db.RunMetrics.AddRange(
            new RunMetric { PipelineRunId = 1, CreatedAt = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc) },
            new RunMetric { PipelineRunId = 1, CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
            new RunMetric { PipelineRunId = 2, CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var metrics = await _repo.GetRunMetricsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, metrics.Count);
        Assert.All(metrics, m => Assert.Equal(1, m.PipelineRunId));
    }

    [Fact]
    public async Task GetCoverageResultsAsync_FiltersByRun()
    {
        _db.CoverageResults.AddRange(
            new CoverageResult { PipelineRunId = 1, CreatedAt = DateTime.UtcNow },
            new CoverageResult { PipelineRunId = 2, CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(await _repo.GetCoverageResultsAsync(1, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetLintResultsAsync_FiltersByRun()
    {
        _db.LintResults.AddRange(
            new LintResult { Id = 11, PipelineRunId = 1, CreatedAt = DateTime.UtcNow },
            new LintResult { Id = 21, PipelineRunId = 2, CreatedAt = DateTime.UtcNow },
            new LintResult { Id = 12, PipelineRunId = 1, CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var results = await _repo.GetLintResultsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal([11, 12], results.Select(r => r.Id).Order());
        Assert.All(results, r => Assert.Equal(1, r.PipelineRunId));
    }

    [Fact]
    public async Task GetRunsPagedAsync_HydratesWarningsAfterEfProjection()
    {
        var pipeline = new Pipeline { Name = "warnings", YamlDefinition = "name: warnings\nstages: []" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineRuns.Add(new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Failed,
            StartedAt = DateTime.UtcNow,
            WarningsJson = """["No online matching agent."]"""
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (runs, _) = await _repo.GetRunsPagedAsync(
            pipeline.Id, 1, 25, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["No online matching agent."], Assert.Single(runs).Warnings);
        Assert.Null(runs[0].ProjectedWarningsJson);
    }

    /// <summary>Server-side duration sorting must order by elapsed length rather than completion
    /// date.</summary>
    [Fact]
    public async Task GetRunsPagedAsync_SortsByDuration_IncludingARunStillGoing()
    {
        var pipeline = new Pipeline { Name = "durations", YamlDefinition = "name: durations\nstages: []" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var start = DateTime.UtcNow.AddHours(-4);
        // Finished last but shortest, so an order by CompletedAt cannot pass for an order by duration.
        _db.PipelineRuns.Add(new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Success,
            StartedAt = start.AddHours(3),
            CompletedAt = start.AddHours(3).AddMinutes(2)
        });
        _db.PipelineRuns.Add(new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Success,
            StartedAt = start,
            CompletedAt = start.AddMinutes(30)
        });
        // Still running for ~4h: measured against now, it is the longest of the three.
        _db.PipelineRuns.Add(new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Running,
            StartedAt = start
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (longestFirst, _) = await _repo.GetRunsPagedAsync(
            pipeline.Id, 1, 25,
            new PipelineRunPaginationRequest { SortBy = "duration", SortDescending = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [PipelineStatus.Running, PipelineStatus.Success, PipelineStatus.Success],
            longestFirst.Select(r => r.Status));
        Assert.Equal(30, (longestFirst[1].CompletedAt!.Value - longestFirst[1].StartedAt).TotalMinutes, 0);
        Assert.Equal(2, (longestFirst[2].CompletedAt!.Value - longestFirst[2].StartedAt).TotalMinutes, 0);

        var (shortestFirst, _) = await _repo.GetRunsPagedAsync(
            pipeline.Id, 1, 25,
            new PipelineRunPaginationRequest { SortBy = "duration", SortDescending = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(PipelineStatus.Running, shortestFirst[^1].Status);
    }

    /// <summary>The grid's generic column filters and sort keys reach the runs query, and the total
    /// they report is the size of the filtered set.</summary>
    [Fact]
    public async Task GetRunsPagedAsync_AppliesTheGridColumnFiltersAndSorts_BeforeCounting()
    {
        var pipeline = new Pipeline { Name = "grid", YamlDefinition = "name: grid\nstages: []" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var start = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        _db.PipelineRuns.AddRange(
            new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Failed, StartedAt = start, BranchName = "feature/login", BuildNumber = 1 },
            new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success, StartedAt = start.AddHours(1), BranchName = "main", BuildNumber = 2 },
            new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Failed, StartedAt = start.AddHours(2), BranchName = "feature/logout", BuildNumber = 3 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (runs, total) = await _repo.GetRunsPagedAsync(
            pipeline.Id, 1, 25,
            new PipelineRunPaginationRequest
            {
                Filters =
                [
                    new GridFilter { Field = "status", Operator = GridFilterOperator.Equals, Value = "Failed" },
                    new GridFilter { Field = "branchName", Operator = GridFilterOperator.StartsWith, Value = "FEATURE/" }
                ],
                Sorts = [new GridSort { Field = "buildNumber", Descending = true }]
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, total);
        Assert.Equal(["feature/logout", "feature/login"], runs.Select(run => run.BranchName));
    }

    /// <summary>The runs grid's checkable status list and its date ranges.</summary>
    [Fact]
    public async Task GetRunsPagedAsync_AppliesTheStatusListAndTheDateRanges()
    {
        var pipeline = new Pipeline { Name = "grid-lists", YamlDefinition = "name: grid\nstages: []" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var start = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        _db.PipelineRuns.AddRange(
            new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Failed, StartedAt = start, CompletedAt = start.AddMinutes(5), BuildNumber = 1 },
            new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Cancelled, StartedAt = start.AddDays(1), CompletedAt = start.AddDays(1).AddMinutes(5), BuildNumber = 2 },
            new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success, StartedAt = start.AddDays(1), CompletedAt = start.AddDays(1).AddMinutes(9), BuildNumber = 3 },
            new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Failed, StartedAt = start.AddDays(3), BuildNumber = 4 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (runs, total) = await _repo.GetRunsPagedAsync(
            pipeline.Id, 1, 25,
            new PipelineRunPaginationRequest
            {
                Filters =
                [
                    new GridFilter { Field = "Status", Operator = GridFilterOperator.In, Value = string.Join(GridFilter.ListSeparator, "Failed", "Cancelled") },
                    new GridFilter
                    {
                        Field = "StartedAt", Operator = GridFilterOperator.GreaterThanOrEqual, Value = "2026-09-01T00:00:00",
                        SecondOperator = GridFilterOperator.LessThan, SecondValue = "2026-09-03T00:00:00"
                    },
                    new GridFilter { Field = "CompletedAt", Operator = GridFilterOperator.GreaterThanOrEqual, Value = "2026-09-02T00:00:00" }
                ]
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, total);
        Assert.Equal(PipelineStatus.Cancelled, Assert.Single(runs).Status);
    }

    [Fact]
    public async Task GetRunsPagedAsync_RefusesAColumnTheGridDoesNotExpose()
    {
        var pipeline = new Pipeline { Name = "grid-refusal", YamlDefinition = "name: grid\nstages: []" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<BadRequestException>(() => _repo.GetRunsPagedAsync(
            pipeline.Id, 1, 25,
            new PipelineRunPaginationRequest { Filters = [new GridFilter { Field = "yamlSnapshot", Operator = GridFilterOperator.Contains, Value = "secret" }] },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetRunsPagedAsync_FiltersBeforeCounting_SoThePagerMatchesWhatIsReachable()
    {
        var pipeline = new Pipeline { Name = "filters", YamlDefinition = "name: filters\nstages: []" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineRuns.Add(new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Failed,
            StartedAt = DateTime.UtcNow,
            BranchName = "develop",
            CommitHash = "abc123"
        });
        _db.PipelineRuns.Add(new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow,
            BranchName = "main",
            CommitHash = "def456"
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (failed, failedTotal) = await _repo.GetRunsPagedAsync(
            pipeline.Id, 1, 25,
            new PipelineRunPaginationRequest { Status = PipelineStatus.Failed },
            TestContext.Current.CancellationToken);
        Assert.Equal(1, failedTotal);
        Assert.Equal("develop", Assert.Single(failed).BranchName);

        var (onBranch, branchTotal) = await _repo.GetRunsPagedAsync(
            pipeline.Id, 1, 25,
            new PipelineRunPaginationRequest { BranchName = "MAIN" },
            TestContext.Current.CancellationToken);
        Assert.Equal(1, branchTotal);
        Assert.Equal("main", Assert.Single(onBranch).BranchName);

        var (byCommit, commitTotal) = await _repo.GetRunsPagedAsync(
            pipeline.Id, 1, 25,
            new PipelineRunPaginationRequest { CommitHash = "abc" },
            TestContext.Current.CancellationToken);
        Assert.Equal(1, commitTotal);
        Assert.Equal("abc123", Assert.Single(byCommit).CommitHash);
    }

    /// <summary>An unknown sort key must fall back to the default order, never reach the database as
    /// an expression built from user input.</summary>
    [Fact]
    public async Task GetRunsPagedAsync_IgnoresAnUnknownSortKey()
    {
        var pipeline = new Pipeline { Name = "hostile", YamlDefinition = "name: hostile\nstages: []" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var newest = DateTime.UtcNow;
        _db.PipelineRuns.Add(new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success, StartedAt = newest.AddHours(-1) });
        _db.PipelineRuns.Add(new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success, StartedAt = newest });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (runs, _) = await _repo.GetRunsPagedAsync(
            pipeline.Id, 1, 25,
            new PipelineRunPaginationRequest { SortBy = "1; drop table pipeline_runs" },
            TestContext.Current.CancellationToken);

        Assert.True(runs[0].StartedAt > runs[1].StartedAt);
    }

    [Fact]
    public async Task IsRunStillRunningAsync_ReflectsStatus()
    {
        _db.PipelineRuns.Add(new PipelineRun { Id = 1, PipelineId = 1, Status = PipelineStatus.Running });
        _db.PipelineRuns.Add(new PipelineRun { Id = 2, PipelineId = 1, Status = PipelineStatus.Success });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await _repo.IsRunStillRunningAsync(1, ct: TestContext.Current.CancellationToken));
        Assert.False(await _repo.IsRunStillRunningAsync(2, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HasAnyFailedStepInRunAsync_IgnoresContinueOnError()
    {
        Step(1, TaskExecutionStatus.Failed, continueOnError: true);
        Assert.False(await _repo.HasAnyFailedStepInRunAsync(1, ct: TestContext.Current.CancellationToken));

        Step(1, TaskExecutionStatus.Failed, continueOnError: false);
        Assert.True(await _repo.HasAnyFailedStepInRunAsync(1, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetContinuableFailedStepsInRunAsync_SeesOnlyTheSwallowedFailures()
    {
        // Continuable failures distinguish Partial from Success.
        Step(1, TaskExecutionStatus.Success);
        Assert.Empty(await _repo.GetContinuableFailedStepsInRunAsync(1, ct: TestContext.Current.CancellationToken));

        Step(1, TaskExecutionStatus.Failed, continueOnError: false);
        Assert.Empty(await _repo.GetContinuableFailedStepsInRunAsync(1, ct: TestContext.Current.CancellationToken));

        Step(1, TaskExecutionStatus.Failed, continueOnError: true);
        Assert.Single(await _repo.GetContinuableFailedStepsInRunAsync(1, ct: TestContext.Current.CancellationToken));
    }

    /// <summary>The rollback stages count as having rolled back only when they ran and all of their
    /// steps succeeded.</summary>
    [Fact]
    public async Task DidStagesAllSucceedAsync_NeedsEveryStepOfTheNamedStagesGreen()
    {
        var ct = TestContext.Current.CancellationToken;
        void StageStep(string stage, TaskExecutionStatus status)
        {
            _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = 7, Status = status, StageName = stage, StepName = "s" });
            _db.SaveChanges();
        }

        StageStep("Deploy", TaskExecutionStatus.Failed);
        Assert.False(await _repo.DidStagesAllSucceedAsync(7, ["Rollback"], ct));

        StageStep("Rollback", TaskExecutionStatus.Success);
        Assert.True(await _repo.DidStagesAllSucceedAsync(7, ["Rollback"], ct));

        StageStep("Rollback", TaskExecutionStatus.Failed);
        Assert.False(await _repo.DidStagesAllSucceedAsync(7, ["Rollback"], ct));
    }

    [Fact]
    public async Task HasAnySucceededStepInRunAsync_IgnoresSystemSteps()
    {
        Step(1, TaskExecutionStatus.Success, system: true);
        Assert.False(await _repo.HasAnySucceededStepInRunAsync(1, ct: TestContext.Current.CancellationToken));

        Step(1, TaskExecutionStatus.Success, system: false);
        Assert.True(await _repo.HasAnySucceededStepInRunAsync(1, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSuccessfulStepOutputsAsync_ReturnsOnlyStepsWithOutputs()
    {
        Step(1, TaskExecutionStatus.Success, outputs: "{\"key\":\"val\"}");
        Step(1, TaskExecutionStatus.Success, outputs: null);
        Step(1, TaskExecutionStatus.Failed, outputs: "{\"x\":\"y\"}");

        var outputs = await _repo.GetSuccessfulStepOutputsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(outputs);
    }

    [Fact]
    public async Task GetSuccessfulStepOutputsAsync_OrdersOutputsByPipelineStepOrder()
    {
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun
            {
                PipelineRunId = 1,
                StageName = "PromoteCurrent",
                StepName = "Current candidate",
                Order = 2,
                Status = TaskExecutionStatus.Success,
                OutputVariablesJson = "{\"CANDIDATE_VERSION\":\"c-current\"}"
            },
            new PipelineStepRun
            {
                PipelineRunId = 1,
                StageName = "Baseline",
                StepName = "Baseline candidate",
                Order = 1,
                Status = TaskExecutionStatus.Success,
                OutputVariablesJson = "{\"CANDIDATE_VERSION\":\"c-baseline\"}"
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var outputs = await _repo.GetSuccessfulStepOutputsAsync(
            1,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(["Baseline", "PromoteCurrent"], outputs.Select(output => output.StageName));
    }

    [Fact]
    public async Task GetComplexityTrendAsync_RunNotFound_ReturnsEmpty()
        => Assert.Empty(await _repo.GetComplexityTrendAsync(999, 10, ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task GetComplexityTrendAsync_ReturnsTrendRowForRunWithMetrics()
    {
        var run = new PipelineRun { Id = 1, PipelineId = 5, Status = PipelineStatus.Success, StartedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) };
        _db.PipelineRuns.Add(run);
        _db.RunMetrics.AddRange(
            new RunMetric { PipelineRunId = 1, PipelineRun = run, Key = "complexity.cyclomatic.avg", Value = 3.5 },
            new RunMetric { PipelineRunId = 1, PipelineRun = run, Key = "complexity.cyclomatic.max", Value = 12 },
            new RunMetric { PipelineRunId = 1, PipelineRun = run, Key = "complexity.crap.avg", Value = 8.2 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var trend = await _repo.GetComplexityTrendAsync(1, 10, ct: TestContext.Current.CancellationToken);

        Assert.Single(trend);
        Assert.Equal(3.5, trend[0].AvgCyclomatic);
        Assert.Equal(12, trend[0].MaxCyclomatic);
    }

    [Fact]
    public async Task GetPipelineIdsWithActiveRunsAsync_ReturnsDistinctActivePipelineIds()
    {
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 1, PipelineId = 10, Status = PipelineStatus.Running },
            new PipelineRun { Id = 2, PipelineId = 10, Status = PipelineStatus.Pending },
            new PipelineRun { Id = 3, PipelineId = 11, Status = PipelineStatus.Success });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ids = await _repo.GetPipelineIdsWithActiveRunsAsync(ct: TestContext.Current.CancellationToken);

        Assert.Contains(10, ids);
        Assert.DoesNotContain(11, ids);
    }

    [Fact]
    public async Task GetStalledSchedulableRunIdsAsync_ReturnsOnlyOldPendingRunsWithoutInflightWork()
    {
        var now = new DateTime(2026, 7, 18, 18, 0, 0, DateTimeKind.Utc);
        var stalledWithDeferredCleanup = new PipelineRun
        {
            Id = 1,
            PipelineId = 10,
            Status = PipelineStatus.Running,
            StartedAt = now.AddMinutes(-10)
        };
        stalledWithDeferredCleanup.Tasks.Add(new ServerTask
        {
            Server = new Server { Name = "cleanup-runner", Hostname = "cleanup-runner" },
            Name = "deferred cleanup",
            Command = "cleanup",
            Status = TaskExecutionStatus.Pending,
            IsDeferredCleanup = true,
            CreatedAt = now.AddMinutes(-10)
        });
        _db.PipelineRuns.AddRange(
            stalledWithDeferredCleanup,
            new PipelineRun { Id = 2, PipelineId = 10, Status = PipelineStatus.Running, StartedAt = now.AddSeconds(-30) },
            new PipelineRun { Id = 3, PipelineId = 10, Status = PipelineStatus.Running, StartedAt = now.AddMinutes(-10) },
            new PipelineRun { Id = 4, PipelineId = 10, Status = PipelineStatus.Success, StartedAt = now.AddMinutes(-10) });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Step(1, TaskExecutionStatus.Pending);
        Step(2, TaskExecutionStatus.Pending);
        Step(3, TaskExecutionStatus.Pending);
        Step(3, TaskExecutionStatus.Running);
        Step(4, TaskExecutionStatus.Pending);

        var ids = await _repo.GetStalledSchedulableRunIdsAsync(
            now.AddMinutes(-2), TestContext.Current.CancellationToken);

        Assert.Equal([1], ids);
    }

    [Fact]
    public async Task GetStalledCancellationRunIdsAsync_ReturnsOnlyOldRequestedRunsWithoutInflightWork()
    {
        var now = new DateTime(2026, 8, 8, 20, 0, 0, DateTimeKind.Utc);
        var cancellationJson = $"{{\"{PipelineRunService.CancellationRequestedVariable}\":\"true\"}}";
        var stalled = new PipelineRun
        {
            Id = 10,
            PipelineId = 10,
            Status = PipelineStatus.Running,
            StartedAt = now.AddMinutes(-10),
            AdditionalVariablesJson = cancellationJson
        };
        stalled.Tasks.Add(new ServerTask
        {
            Server = new Server { Name = "cleanup-runner", Hostname = "cleanup-runner" },
            Name = "deferred cleanup",
            Command = "cleanup",
            Status = TaskExecutionStatus.Pending,
            IsDeferredCleanup = true,
            CreatedAt = now.AddMinutes(-10)
        });
        var active = new PipelineRun
        {
            Id = 11,
            PipelineId = 10,
            Status = PipelineStatus.Running,
            StartedAt = now.AddMinutes(-10),
            AdditionalVariablesJson = cancellationJson
        };
        active.Tasks.Add(new ServerTask
        {
            Server = new Server { Name = "active-runner", Hostname = "active-runner" },
            Name = "active work",
            Command = "work",
            Status = TaskExecutionStatus.Running,
            CreatedAt = now.AddMinutes(-10)
        });
        _db.PipelineRuns.AddRange(
            stalled,
            active,
            new PipelineRun
            {
                Id = 12,
                PipelineId = 10,
                Status = PipelineStatus.Running,
                StartedAt = now.AddMinutes(-10),
                AdditionalVariablesJson = "{}"
            },
            new PipelineRun
            {
                Id = 13,
                PipelineId = 10,
                Status = PipelineStatus.Running,
                StartedAt = now.AddSeconds(-30),
                AdditionalVariablesJson = cancellationJson
            });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ids = await _repo.GetStalledCancellationRunIdsAsync(
            now.AddMinutes(-2), TestContext.Current.CancellationToken);

        Assert.Equal([10], ids);
    }

    [Fact]
    public async Task HasAnyRunningStepInRunAsync_TrueWhenRunningStep()
    {
        Step(1, TaskExecutionStatus.Running);
        Assert.True(await _repo.HasAnyRunningStepInRunAsync(1, ct: TestContext.Current.CancellationToken));
        Assert.False(await _repo.HasAnyRunningStepInRunAsync(2, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetWebhookTriggeredPipelinesForProjectAsync_ReturnsOnlyWebhookPipelines()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Id = 1, Name = "hook", ProjectId = 1, TriggerType = PipelineTriggerType.Webhook },
            new Pipeline { Id = 2, Name = "manual", ProjectId = 1, TriggerType = PipelineTriggerType.Manual });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetWebhookTriggeredPipelinesForProjectAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("hook", result[0].Name);
    }

    [Fact]
    public async Task GetEnvironmentCopyTargetAsync_ReturnsNameAndProject()
    {
        _db.Environments.Add(new Aetheus.Back.Data.Entities.Environment { Id = 1, Name = "prod", ProjectId = 9 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var target = await _repo.GetEnvironmentCopyTargetAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(target);
        Assert.Equal("prod", target.Value.Name);
        Assert.Equal(9, target.Value.ProjectId);
        Assert.Null(await _repo.GetEnvironmentCopyTargetAsync(999, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindServerIdsInPoolAsync_ReturnsServerIdsForPool()
    {
        // The link fixture requires its server row because retirement filtering resolves that
        // relation.
        _db.Servers.Add(new Server { Id = 7, Name = "pool-runner", Hostname = "pool-runner" });
        _db.AgentPoolServers.Add(new AgentPoolServer { ServerId = 7, AgentPool = new AgentPool { Id = 1, Name = "build-pool" } });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ids = await _repo.FindServerIdsInPoolAsync("build-pool", ct: TestContext.Current.CancellationToken);

        Assert.Contains(7, ids);
    }

    [Fact]
    public async Task FindServerIdsInEnvironmentAsync_ReturnsServerIdsForEnvironment()
    {
        _db.Servers.Add(new Server { Id = 8, Name = "env-runner", Hostname = "env-runner" });
        _db.EnvironmentServers.Add(new EnvironmentServer { ServerId = 8, Environment = new Aetheus.Back.Data.Entities.Environment { Id = 1, Name = "staging" } });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ids = await _repo.FindServerIdsInEnvironmentAsync("staging", projectId: null, ct: TestContext.Current.CancellationToken);

        Assert.Contains(8, ids);
    }

    [Fact]
    public async Task FindOnlineServerByIdAsync_ReturnsOnlyEnabledOnlineRunner()
    {
        _db.Servers.AddRange(
            new Server { Id = 1, Name = "on", Hostname = "on", Status = ServerStatus.Online, PipelineRunnerEnabled = true, AgentProtocolVersion = AgentProtocol.CurrentVersion, AgentCapabilitiesJson = "[\"pipeline.build\"]" },
            new Server { Id = 2, Name = "off", Hostname = "off", Status = ServerStatus.Offline, PipelineRunnerEnabled = true, AgentProtocolVersion = AgentProtocol.CurrentVersion, AgentCapabilitiesJson = "[\"pipeline.build\"]" },
            new Server { Id = 3, Name = "inventory", Hostname = "inventory", Status = ServerStatus.Online, PipelineRunnerEnabled = false },
            new Server { Id = 4, Name = "previous-protocol", Hostname = "previous-protocol", Status = ServerStatus.Online, PipelineRunnerEnabled = true, AgentProtocolVersion = AgentProtocol.CurrentVersion - 1, AgentCapabilitiesJson = "[\"pipeline.build\"]" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(await _repo.FindOnlineServerByIdAsync(1, ct: TestContext.Current.CancellationToken));
        Assert.Null(await _repo.FindOnlineServerByIdAsync(2, ct: TestContext.Current.CancellationToken));
        Assert.Null(await _repo.FindOnlineServerByIdAsync(3, ct: TestContext.Current.CancellationToken));
        Assert.Null(await _repo.FindOnlineServerByIdAsync(4, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindCandidateTargetServerIdsAsync_NoSelectorIncludesOnlyOrganizationFallbackRunners()
    {
        _db.Servers.AddRange(
            new Server { Id = 1, Name = "org-runner", Hostname = "one", OrganizationId = 10, PipelineRunnerEnabled = true },
            new Server { Id = 2, Name = "foreign-runner", Hostname = "two", OrganizationId = 20, PipelineRunnerEnabled = true },
            new Server { Id = 3, Name = "not-runner", Hostname = "three", OrganizationId = 10, PipelineRunnerEnabled = false });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ids = await _repo.FindCandidateTargetServerIdsAsync(
            null, null, null, OsType.Unknown, 10, deploymentStage: false,
            projectId: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal([1], ids);
    }

    [Fact]
    public async Task FindCandidateTargetServerIdsAsync_MissingExplicitRunnerSelectorDoesNotFallback()
    {
        _db.Servers.Add(new Server
        {
            Id = 1,
            Name = "org-runner",
            Hostname = "one",
            OrganizationId = 10,
            PipelineRunnerEnabled = true
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ids = await _repo.FindCandidateTargetServerIdsAsync(
            null, null, "missing", OsType.Unknown, 10, deploymentStage: false,
            projectId: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Empty(ids);
    }

    [Fact]
    public async Task FindCandidateTargetServerIdsAsync_MissingExplicitDeploySelectorDoesNotFallback()
    {
        _db.Servers.Add(new Server
        {
            Id = 1,
            Name = "prod",
            Hostname = "prod",
            OrganizationId = 10,
            DeploymentTargetAvailable = true
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ids = await _repo.FindCandidateTargetServerIdsAsync(
            null, "missing", null, OsType.Unknown, 10, deploymentStage: true, projectId: null, ct: TestContext.Current.CancellationToken);

        Assert.Empty(ids);
    }

    [Fact]
    public async Task ExplicitAgentSelector_IsScopedToPipelineOrganization()
    {
        _db.Servers.AddRange(
            new Server { Id = 1, Name = "shared", Hostname = "foreign", OrganizationId = 20, Status = ServerStatus.Online, PipelineRunnerEnabled = true, AgentProtocolVersion = AgentProtocol.CurrentVersion, AgentCapabilitiesJson = "[\"pipeline.build\"]" },
            new Server { Id = 2, Name = "shared", Hostname = "owned", OrganizationId = 10, Status = ServerStatus.Online, PipelineRunnerEnabled = true, AgentProtocolVersion = AgentProtocol.CurrentVersion, AgentCapabilitiesJson = "[\"pipeline.build\"]" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var server = await _repo.FindOnlineServerByAgentInOrganizationAsync("shared", OsType.Unknown, 10, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(server);
        Assert.Equal(2, server.Id);
    }

    [Fact]
    public async Task ExplicitPoolSelector_IsScopedToPipelineOrganization()
    {
        var pool = new AgentPool { Id = 1, Name = "shared-pool" };
        var foreign = new Server { Id = 1, Name = "foreign", Hostname = "foreign", OrganizationId = 20, Status = ServerStatus.Online, PipelineRunnerEnabled = true, AgentProtocolVersion = AgentProtocol.CurrentVersion, AgentCapabilitiesJson = "[\"pipeline.build\"]" };
        var owned = new Server { Id = 2, Name = "owned", Hostname = "owned", OrganizationId = 10, Status = ServerStatus.Online, PipelineRunnerEnabled = true, AgentProtocolVersion = AgentProtocol.CurrentVersion, AgentCapabilitiesJson = "[\"pipeline.build\"]" };
        _db.AgentPoolServers.AddRange(
            new AgentPoolServer { AgentPool = pool, Server = foreign },
            new AgentPoolServer { AgentPool = pool, Server = owned });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var server = await _repo.FindOnlineServerInPoolInOrganizationAsync("shared-pool", OsType.Unknown, 10, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(server);
        Assert.Equal(2, server.Id);
    }

    [Fact]
    public async Task ExplicitDeployEnvironment_IsScopedToPipelineOrganization()
    {
        var environment = new Aetheus.Back.Data.Entities.Environment { Id = 1, Name = "prod" };
        var foreign = new Server { Id = 1, Name = "foreign", Hostname = "foreign", OrganizationId = 20, Status = ServerStatus.Online, DeploymentTargetAvailable = true, AgentProtocolVersion = AgentProtocol.CurrentVersion, AgentCapabilitiesJson = "[\"deployment.apply\"]" };
        var owned = new Server { Id = 2, Name = "owned", Hostname = "owned", OrganizationId = 10, Status = ServerStatus.Online, DeploymentTargetAvailable = true, AgentProtocolVersion = AgentProtocol.CurrentVersion, AgentCapabilitiesJson = "[\"deployment.apply\"]" };
        _db.EnvironmentServers.AddRange(
            new EnvironmentServer { Environment = environment, Server = foreign },
            new EnvironmentServer { Environment = environment, Server = owned });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var server = await _repo.FindOnlineDeployTargetAsync(null, "prod", null, OsType.Unknown, 10, projectId: null, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(server);
        Assert.Equal(2, server.Id);
    }

    [Fact]
    public async Task GetStageProducerServerIdAsync_ReturnsSuccessfulServerFromRequestedStage()
    {
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { Id = 1, PipelineRunId = 7, StageName = "build", StepName = "compile", ServerId = 11, Status = TaskExecutionStatus.Success },
            new PipelineStepRun { Id = 2, PipelineRunId = 7, StageName = "deploy", StepName = "ship", ServerId = 22, Status = TaskExecutionStatus.Success });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(11, await _repo.GetStageProducerServerIdAsync(7, "build", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPipelinesByEnvironmentAsync_FiltersByEnvironment()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Id = 1, Name = "a", EnvironmentId = 5 },
            new Pipeline { Id = 2, Name = "b", EnvironmentId = 6 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(await _repo.GetPipelinesByEnvironmentAsync(5, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPipelineIdForArtifactAsync_ResolvesThroughRun()
    {
        var run = new PipelineRun { Id = 1, PipelineId = 42, Status = PipelineStatus.Success };
        _db.PipelineArtifacts.Add(new PipelineArtifact { Id = 1, PipelineRunId = 1, PipelineId = 42, Name = "a", FilePath = "/a", PipelineRun = run });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(42, await _repo.GetPipelineIdForArtifactAsync(1, ct: TestContext.Current.CancellationToken));
        Assert.Null(await _repo.GetPipelineIdForArtifactAsync(999, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPipelineIdForApprovalAsync_ResolvesThroughRun()
    {
        var run = new PipelineRun { Id = 1, PipelineId = 55, Status = PipelineStatus.WaitingForApproval };
        _db.PipelineApprovals.Add(new PipelineApproval { Id = 1, PipelineRunId = 1, PipelineRun = run });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(55, await _repo.GetPipelineIdForApprovalAsync(1, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryTransitionPipelineRunStatusAsync_RequiresExpectedSourceStatus()
    {
        _db.PipelineRuns.Add(new PipelineRun { Id = 1, PipelineId = 55, Status = PipelineStatus.WaitingForApproval });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(await _repo.TryTransitionPipelineRunStatusAsync(
            1, PipelineStatus.Running, PipelineStatus.Cancelled, ct: TestContext.Current.CancellationToken));
        Assert.True(await _repo.TryTransitionPipelineRunStatusAsync(
            1, PipelineStatus.WaitingForApproval, PipelineStatus.Cancelled, ct: TestContext.Current.CancellationToken));
        Assert.Equal(PipelineStatus.Cancelled, (await _db.PipelineRuns.FindAsync([1], TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task GetTriggeredChildRunIdsAsync_ReturnsDistinctDirectChildrenOnly()
    {
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = 1, StageName = "release", StepName = "ci", TriggeredRunId = 2 },
            new PipelineStepRun { PipelineRunId = 1, StageName = "release", StepName = "ci-again", TriggeredRunId = 2 },
            new PipelineStepRun { PipelineRunId = 1, StageName = "release", StepName = "qa", TriggeredRunId = 3 },
            new PipelineStepRun { PipelineRunId = 2, StageName = "ci", StepName = "nested", TriggeredRunId = 4 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var children = await _repo.GetTriggeredChildRunIdsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal([2, 3], children.OrderBy(id => id));
    }

    [Fact]
    public async Task GetActiveRunIdsAsync_ReturnsOnlyNonTerminalRunsForPipeline()
    {
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 1, PipelineId = 10, Status = PipelineStatus.Running },
            new PipelineRun { Id = 2, PipelineId = 10, Status = PipelineStatus.WaitingForApproval },
            new PipelineRun { Id = 3, PipelineId = 10, Status = PipelineStatus.Success },
            new PipelineRun { Id = 4, PipelineId = 11, Status = PipelineStatus.Running });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ids = await _repo.GetActiveRunIdsAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], ids);
    }

    [Fact]
    public async Task CancellationRequest_CancelsOrdinaryTailButPreservesMandatoryTeardown()
    {
        _db.PipelineRuns.Add(new PipelineRun
        {
            Id = 1,
            PipelineId = 55,
            Status = PipelineStatus.Running,
            AdditionalVariablesJson = "{}"
        });
        var workStep = new PipelineStepRun
        {
            PipelineRunId = 1,
            StageName = "Work",
            StepName = "pending",
            Status = TaskExecutionStatus.Pending
        };
        var teardownStep = new PipelineStepRun
        {
            PipelineRunId = 1,
            StageName = "Teardown",
            StepName = "cleanup",
            Status = TaskExecutionStatus.Pending
        };
        var systemCleanupStep = new PipelineStepRun
        {
            PipelineRunId = 1,
            StageName = PipelineRunService.SystemCleanupStage,
            StepName = "workspace",
            Status = TaskExecutionStatus.Pending,
            IsSystem = true
        };
        var racedStep = new PipelineStepRun
        {
            PipelineRunId = 1,
            StageName = "Work",
            StepName = "started-during-cancellation",
            Status = TaskExecutionStatus.Running
        };
        var orphanStep = new PipelineStepRun
        {
            PipelineRunId = 1,
            StageName = "Work",
            StepName = "running-without-task",
            Status = TaskExecutionStatus.Running
        };
        _db.PipelineStepRuns.AddRange(workStep, teardownStep, systemCleanupStep, racedStep, orphanStep);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.Tasks.AddRange(
            new ServerTask
            {
                ServerId = 1,
                PipelineRunId = 1,
                PipelineStepRunId = workStep.Id,
                Name = "work",
                Status = TaskExecutionStatus.Pending
            },
            new ServerTask
            {
                ServerId = 1,
                PipelineRunId = 1,
                PipelineStepRunId = teardownStep.Id,
                Name = "teardown",
                Status = TaskExecutionStatus.Pending
            },
            new ServerTask
            {
                ServerId = 1,
                PipelineRunId = 1,
                PipelineStepRunId = null,
                Name = "Collect Artifacts (BuildArtifacts)",
                Operation = OperationKind.PipelineCollectArtifacts,
                Status = TaskExecutionStatus.Pending
            },
            new ServerTask
            {
                ServerId = 1,
                PipelineRunId = 1,
                PipelineStepRunId = racedStep.Id,
                Name = "started-during-cancellation",
                Status = TaskExecutionStatus.Cancelled
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.CancelPendingStepRunsExceptStagesAsync(
            1,
            ["Teardown", PipelineRunService.SystemCleanupStage],
            TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Running, orphanStep.Status);

        await _repo.CancelOrphanedRunningStepRunsExceptStagesAsync(
            1,
            ["Teardown", PipelineRunService.SystemCleanupStage],
            TestContext.Current.CancellationToken);

        var variables = PipelineRunHelpers.DeserializeResolvedVariables(
            (await _db.PipelineRuns.FindAsync([1], TestContext.Current.CancellationToken))!.AdditionalVariablesJson);
        Assert.Equal("true", variables[PipelineRunService.CancellationRequestedVariable]);
        var steps = await _db.PipelineStepRuns
            .OrderBy(step => step.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskExecutionStatus.Cancelled, steps.Single(step => step.StepName == "pending").Status);
        Assert.Equal(
            TaskExecutionStatus.Cancelled,
            steps.Single(step => step.StepName == "started-during-cancellation").Status);
        Assert.Equal(
            TaskExecutionStatus.Cancelled,
            steps.Single(step => step.StepName == "running-without-task").Status);
        Assert.Equal(TaskExecutionStatus.Pending, steps.Single(step => step.StageName == "Teardown").Status);
        Assert.Equal(
            TaskExecutionStatus.Pending,
            steps.Single(step => step.StageName == PipelineRunService.SystemCleanupStage).Status);
        var tasks = await _db.Tasks.OrderBy(task => task.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskExecutionStatus.Cancelled, tasks.Single(task => task.Name == "work").Status);
        Assert.Equal(TaskExecutionStatus.Pending, tasks.Single(task => task.Name == "teardown").Status);
        Assert.Equal(
            TaskExecutionStatus.Cancelled,
            tasks.Single(task => task.Operation == OperationKind.PipelineCollectArtifacts).Status);
    }

    [Fact]
    public async Task DependencyQueries_ProjectFiveNewestRuns()
    {
        _db.Pipelines.Add(new Pipeline { Id = 70, Name = "history" });
        _db.PipelineRuns.AddRange(Enumerable.Range(1, 6).Select(id => new PipelineRun
        {
            Id = id,
            PipelineId = 70,
            Status = id % 2 == 0 ? PipelineStatus.Success : PipelineStatus.Failed,
            StartedAt = new DateTime(2026, 7, 1, 0, id, 0, DateTimeKind.Utc)
        }));
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var graphItem = Assert.Single(await _repo.GetPipelinesForDependencyGraphAsync(ct: TestContext.Current.CancellationToken));
        var (pageItems, _, _) = await _repo.GetPipelineDependencyPageAsync(
            new PipelinePaginationRequest { Page = 1, PageSize = 25 }, ct: TestContext.Current.CancellationToken);
        var pageItem = Assert.Single(pageItems);

        Assert.Equal([6, 5, 4, 3, 2], graphItem.RecentRuns.Select(run => run.Id));
        Assert.Equal([6, 5, 4, 3, 2], pageItem.RecentRuns.Select(run => run.Id));
    }

    [Fact]
    public async Task DependencyQueries_CarryTheGradeOfEachRecentRun()
    {
        // The pipelines table shows the grade the run grids show. Run 1 sealed its own assurance
        // letter; run 2 grades nothing and must stay without one.
        _db.Pipelines.Add(new Pipeline { Id = 71, Name = "candidate" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 11, PipelineId = 71, Status = PipelineStatus.Success, StartedAt = new DateTime(2026, 7, 1, 0, 1, 0, DateTimeKind.Utc) },
            new PipelineRun { Id = 12, PipelineId = 71, Status = PipelineStatus.Success, StartedAt = new DateTime(2026, 7, 1, 0, 2, 0, DateTimeKind.Utc) });
        _db.PipelineStepRuns.Add(new PipelineStepRun
        {
            PipelineRunId = 11,
            StepName = "seal",
            Status = TaskExecutionStatus.Success,
            OutputVariablesJson = "{\"CANDIDATE_ASSURANCE_GRADE\":\"C\"}"
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var graphItem = Assert.Single(await _repo.GetPipelinesForDependencyGraphAsync(ct: TestContext.Current.CancellationToken));
        var (pageItems, _, _) = await _repo.GetPipelineDependencyPageAsync(
            new PipelinePaginationRequest { Page = 1, PageSize = 25 }, ct: TestContext.Current.CancellationToken);

        foreach (var runs in new[] { graphItem.RecentRuns, Assert.Single(pageItems).RecentRuns })
        {
            Assert.Equal(AnalysisGrade.C, runs.Single(run => run.Id == 11).GateGrade);
            Assert.Null(runs.Single(run => run.Id == 12).GateGrade);
        }
    }

    [Fact]
    public async Task DependencyGraph_ServerScope_ReturnsOnlyPipelinesExecutedOnServer()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Id = 80, Name = "used", YamlDefinition = "name: used\nstages: []" },
            new Pipeline { Id = 81, Name = "unused", YamlDefinition = "name: unused\nstages: []" });
        _db.PipelineRuns.Add(new PipelineRun { Id = 80, PipelineId = 80, Status = PipelineStatus.Success });
        _db.PipelineStepRuns.Add(new PipelineStepRun
        {
            PipelineRunId = 80,
            ServerId = 12,
            StepName = "deploy",
            Status = TaskExecutionStatus.Success
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var pipelines = await _repo.GetPipelinesForDependencyGraphAsync(
            serverId: 12, ct: TestContext.Current.CancellationToken);

        Assert.Equal("used", Assert.Single(pipelines).Name);
    }
    [Fact]
    public async Task DependencyGraph_ProjectScope_ReturnsOnlyThatProjectsPipelines()
    {
        // Project-scoped graph reads exclude unrelated fleet pipelines.
        _db.Projects.AddRange(
            new Project { Id = 90, Name = "mine", OrganizationId = 1 },
            new Project { Id = 91, Name = "other", OrganizationId = 1 });
        _db.Environments.Add(new Aetheus.Back.Data.Entities.Environment { Id = 92, Name = "staging", ProjectId = 90 });
        _db.Pipelines.AddRange(
            new Pipeline { Id = 90, Name = "mine-direct", ProjectId = 90, YamlDefinition = "name: a" },
            new Pipeline { Id = 92, Name = "mine-via-environment", EnvironmentId = 92, YamlDefinition = "name: b" },
            new Pipeline { Id = 91, Name = "theirs", ProjectId = 91, YamlDefinition = "name: c" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var pipelines = await _repo.GetPipelinesForDependencyGraphAsync(
            projectId: 90, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, pipelines.Count);
        Assert.DoesNotContain(pipelines, pipeline => pipeline.Name == "theirs");
    }


    [Fact]
    public async Task TryResolveApprovalAsync_OnlyFirstPendingDecisionWins()
    {
        var run = new PipelineRun { Id = 1, PipelineId = 55, Status = PipelineStatus.WaitingForApproval };
        var environment = new Aetheus.Back.Data.Entities.Environment { Id = 1, Name = "production" };
        _db.PipelineRuns.Add(run);
        _db.Environments.Add(environment);
        _db.PipelineApprovals.Add(new PipelineApproval
        {
            Id = 1,
            PipelineRunId = 1,
            PipelineRun = run,
            EnvironmentId = 1,
            Environment = environment,
            StageName = "deploy",
            Status = ApprovalStatus.Pending
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var first = await _repo.TryResolveApprovalAsync(
            1, ApprovalStatus.Approved, DateTime.UtcNow, null, "approved", ct: TestContext.Current.CancellationToken);
        var second = await _repo.TryResolveApprovalAsync(
            1, ApprovalStatus.Rejected, DateTime.UtcNow, null, "rejected", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Equal(ApprovalStatus.Approved, first.Status);
        Assert.Null(second);
    }

    private void SeedApproval(int id, int runId, int environmentId, ApprovalStatus status, DateTime requestedAt)
    {
        _db.PipelineApprovals.Add(new PipelineApproval
        {
            Id = id,
            PipelineRunId = runId,
            EnvironmentId = environmentId,
            StageName = "deploy",
            Status = status,
            RequestedAt = requestedAt
        });
    }

    [Fact]
    public async Task GetPendingApprovalsAsync_ListsOnlyUndecidedApprovalsOfWaitingRunsTheCallerMayRead()
    {
        // The home page and the top bar list what waits for a decision, and only that.
        var at = new DateTime(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc);
        _db.Environments.Add(new Aetheus.Back.Data.Entities.Environment { Id = 1, Name = "prod" });
        _db.Pipelines.AddRange(
            new Pipeline { Id = 60, Name = "deploy-prod", ProjectId = 3 },
            new Pipeline { Id = 61, Name = "someone-elses" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 600, PipelineId = 60, Status = PipelineStatus.WaitingForApproval },
            new PipelineRun { Id = 601, PipelineId = 60, Status = PipelineStatus.Failed },
            new PipelineRun { Id = 602, PipelineId = 61, Status = PipelineStatus.WaitingForApproval },
            new PipelineRun { Id = 603, PipelineId = 60, Status = PipelineStatus.WaitingForApproval });
        SeedApproval(1, runId: 600, environmentId: 1, ApprovalStatus.Pending, requestedAt: at);
        SeedApproval(2, runId: 601, environmentId: 1, ApprovalStatus.Pending, requestedAt: at);   // run ended
        SeedApproval(3, runId: 602, environmentId: 1, ApprovalStatus.Pending, requestedAt: at);   // not readable
        SeedApproval(4, runId: 603, environmentId: 1, ApprovalStatus.Approved, requestedAt: at);  // decided
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var pending = await _repo.GetPendingApprovalsAsync([60], TestContext.Current.CancellationToken);

        var approval = Assert.Single(pending);
        Assert.Equal(1, approval.ApprovalId);
        Assert.Equal(600, approval.PipelineRunId);
        Assert.Equal("deploy-prod", approval.PipelineName);
        Assert.Equal(3, approval.ProjectId);
        Assert.Equal("prod", approval.EnvironmentName);
        Assert.Equal(2, (await _repo.GetPendingApprovalsAsync(null, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task GetExpiredPendingApprovalIdsAsync_SelectsOnlyPendingApprovalsPastTheirEnvironmentTimeout()
    {
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        _db.Environments.Add(new Aetheus.Back.Data.Entities.Environment
        {
            Id = 1,
            Name = "production",
            ApprovalTimeoutMinutes = 30
        });
        SeedApproval(1, runId: 10, environmentId: 1, ApprovalStatus.Pending, requestedAt: now.AddMinutes(-60));
        SeedApproval(2, runId: 11, environmentId: 1, ApprovalStatus.Pending, requestedAt: now.AddMinutes(-5));
        SeedApproval(3, runId: 12, environmentId: 1, ApprovalStatus.Approved, requestedAt: now.AddMinutes(-60));
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var expired = await _repo.GetExpiredPendingApprovalIdsAsync(now, TestContext.Current.CancellationToken);

        Assert.Equal([1], expired);
    }

    /// <summary>Stage confirmation expires according to its own window.</summary>
    [Fact]
    public async Task GetExpiredPendingApprovalIdsAsync_AStageDelay_OverridesTheEnvironmentTimeout()
    {
        var now = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
        _db.Environments.Add(new Aetheus.Back.Data.Entities.Environment { Id = 1, Name = "production", ApprovalTimeoutMinutes = 1440 });
        SeedApproval(1, runId: 10, environmentId: 1, ApprovalStatus.Pending, requestedAt: now.AddMinutes(-11));
        SeedApproval(2, runId: 11, environmentId: 1, ApprovalStatus.Pending, requestedAt: now.AddMinutes(-9));
        SeedApproval(3, runId: 12, environmentId: 1, ApprovalStatus.Pending, requestedAt: now.AddMinutes(-11));
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        foreach (var approval in _db.PipelineApprovals.Where(a => a.Id == 1 || a.Id == 2))
            approval.TimeoutMinutes = 10;
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // 1: past its ten minutes. 2: within them. 3: no delay of its own, within the environment's day.
        Assert.Equal([1], await _repo.GetExpiredPendingApprovalIdsAsync(now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetExpiredPendingApprovalIdsAsync_WithinTimeout_ReturnsNothing()
    {
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        // Default entity timeout (1440 min): a day-old request is still within it.
        _db.Environments.Add(new Aetheus.Back.Data.Entities.Environment { Id = 1, Name = "staging" });
        SeedApproval(1, runId: 10, environmentId: 1, ApprovalStatus.Pending, requestedAt: now.AddMinutes(-1439));
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(await _repo.GetExpiredPendingApprovalIdsAsync(now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetExpiredPendingApprovalIdsAsync_ZeroTimeout_ExpiresImmediately()
    {
        // Documents the real contract: the query has no "0 = disabled" special case, so a raw 0 expires
        // any past request immediately. 0 is unreachable through the API ([Range(1, 10080)] on
        // EnvironmentRequest.ApprovalTimeoutMinutes) and the entity defaults to 1440.
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        _db.Environments.Add(new Aetheus.Back.Data.Entities.Environment
        {
            Id = 1,
            Name = "raw-zero",
            ApprovalTimeoutMinutes = 0
        });
        SeedApproval(1, runId: 10, environmentId: 1, ApprovalStatus.Pending, requestedAt: now.AddMinutes(-1));
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([1], await _repo.GetExpiredPendingApprovalIdsAsync(now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredApproval_TimedOutDecisionThroughRealRepository_FailsRunAndLeavesFreshApprovalUntouched()
    {
        // The exact sequence ExpireStaleApprovalsAsync performs, against the real repository: select the
        // expired ids, then decide each TimedOut through PipelineApprovalService.
        var now = DateTime.UtcNow;
        _db.Environments.Add(new Aetheus.Back.Data.Entities.Environment
        {
            Id = 1,
            Name = "production",
            ApprovalTimeoutMinutes = 30
        });
        _db.PipelineRuns.Add(new PipelineRun { Id = 10, PipelineId = 55, Status = PipelineStatus.WaitingForApproval });
        _db.PipelineRuns.Add(new PipelineRun { Id = 11, PipelineId = 55, Status = PipelineStatus.WaitingForApproval });
        SeedApproval(1, runId: 10, environmentId: 1, ApprovalStatus.Pending, requestedAt: now.AddMinutes(-60));
        SeedApproval(2, runId: 11, environmentId: 1, ApprovalStatus.Pending, requestedAt: now.AddMinutes(-5));
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // The refusal goes through the real control service against the real repository: an environment
        // approval carries no delay of its own, so the run fails without its scheduler being involved.
        var runService = Substitute.For<IPipelineRunService>();
        var control = new PipelineRunControlService(
            _repo, Substitute.For<IHubContext<PipelineHub>>(), Substitute.For<IPipelineRunDefinitionParser>(), Substitute.For<Aetheus.Back.Components.Audit.IAuditService>());
        runService.ApplyRefusalAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => control.ApplyRefusalAsync(
                call.Arg<int>(), Substitute.For<IPipelineRunScheduler>(), Substitute.For<IPipelineChildRunLauncher>(),
                call.Arg<CancellationToken>()));
        var approvalService = new PipelineApprovalService(
            _repo,
            _repo,
            runService,
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IHubContext<PipelineHub>>(),
            Substitute.For<Aetheus.Back.Components.Audit.IAuditService>(),
            NullLogger<PipelineApprovalService>.Instance,
            TimeProvider.System);

        var expired = await _repo.GetExpiredPendingApprovalIdsAsync(now, TestContext.Current.CancellationToken);
        var decidedId = Assert.Single(expired);
        var decided = await approvalService.DecideApprovalAsync(
            decidedId,
            new ApprovalDecisionRequest { Decision = ApprovalStatus.TimedOut, Comments = "Approval timed out" },
            TestContext.Current.CancellationToken);

        Assert.NotNull(decided);
        Assert.Equal(ApprovalStatus.TimedOut, decided.Status);
        Assert.NotNull(decided.ResolvedAt);
        Assert.Equal("Approval timed out", decided.Comments);

        var expiredApproval = await _db.PipelineApprovals.AsNoTracking()
            .FirstAsync(a => a.Id == 1, TestContext.Current.CancellationToken);
        Assert.Equal(ApprovalStatus.TimedOut, expiredApproval.Status);
        var failedRun = await _db.PipelineRuns.AsNoTracking()
            .FirstAsync(r => r.Id == 10, TestContext.Current.CancellationToken);
        Assert.Equal(PipelineStatus.Failed, failedRun.Status);
        Assert.NotNull(failedRun.CompletedAt);

        var freshApproval = await _db.PipelineApprovals.AsNoTracking()
            .FirstAsync(a => a.Id == 2, TestContext.Current.CancellationToken);
        Assert.Equal(ApprovalStatus.Pending, freshApproval.Status);
        var untouchedRun = await _db.PipelineRuns.AsNoTracking()
            .FirstAsync(r => r.Id == 11, TestContext.Current.CancellationToken);
        Assert.Equal(PipelineStatus.WaitingForApproval, untouchedRun.Status);
    }

    /// <summary>The shipped catalog lives in the platform organization and is shared by name; a
    /// template someone wrote there, or one of another organization, is not.</summary>
    [Fact]
    public async Task FindSharedTemplateByNameAsync_SharesOnlyThePlatformsShippedTemplates()
    {
        var platform = new Organization { Id = 1, Name = "Aetheus", Slug = "aetheus" };
        var other = new Organization { Id = 2, Name = "Client", Slug = "client" };
        _db.Organizations.AddRange(platform, other);
        _db.PipelineTemplates.AddRange(
            new PipelineTemplate { Id = 1, Name = "application-ci", OrganizationId = 1 },
            new PipelineTemplate { Id = 2, Name = "my-private-template", OrganizationId = 1 },
            new PipelineTemplate { Id = 3, Name = "application-qa", OrganizationId = 2 });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(1, (await _repo.FindSharedTemplateByNameAsync("Application-CI", ct))?.Id);
        Assert.Null(await _repo.FindSharedTemplateByNameAsync("my-private-template", ct));
        Assert.Null(await _repo.FindSharedTemplateByNameAsync("application-qa", ct));
    }
}
