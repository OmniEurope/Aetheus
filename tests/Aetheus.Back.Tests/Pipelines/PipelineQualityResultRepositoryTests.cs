// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The quality-result and artifact ports and the run lookups (AUD-07, suite) answer what the
/// result submission service and the metrics handler read and write, on real rows, and the
/// IPipelineRepository facade, which delegates to them, answers the same.</summary>
public sealed class PipelineQualityResultRepositoryTests : IDisposable
{
    private static readonly DateTime At = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IPipelineRepository Facade() => new PipelineRepository(
        _db, TimeProvider.System, NullLogger<PipelineRepository>.Instance,
        new PipelineTaskLifecycleRepository(_db, TimeProvider.System), new PipelineRunLineageRepository(_db));

    private async Task SeedAsync()
    {
        _db.Pipelines.Add(new Pipeline { Id = 1, Name = "ci" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 10, PipelineId = 1, Status = PipelineStatus.Success, StartedAt = At },
            new PipelineRun { Id = 11, PipelineId = 1, Status = PipelineStatus.Running, StartedAt = At.AddHours(1) });
        await _db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task TheQualityPort_StoresAndReadsBackTheResultsOfARun()
    {
        await SeedAsync();
        IPipelineQualityResultRepository port = new PipelineCoverageRepository(_db);

        Assert.Null(await port.GetTestResultSummaryAsync(10, Ct));
        await port.AddTestResultsAsync(
        [
            new TestResult { PipelineRunId = 10, TestSuite = "B", TestName = "late", Outcome = TestOutcome.Failed, DurationMs = 30 },
            new TestResult { PipelineRunId = 10, TestSuite = "A", TestName = "z", Outcome = TestOutcome.Passed, DurationMs = 10 },
            new TestResult { PipelineRunId = 10, TestSuite = "A", TestName = "a", Outcome = TestOutcome.Passed, DurationMs = 5 },
            new TestResult { PipelineRunId = 11, TestSuite = "A", TestName = "other run", Outcome = TestOutcome.Skipped }
        ], Ct);
        Assert.Equal(["a", "z", "late"], (await port.GetTestResultsAsync(10, Ct)).Select(t => t.TestName));
        var summary = await port.GetTestResultSummaryAsync(10, Ct);
        Assert.NotNull(summary);
        Assert.Equal((3, 2, 1, 0), (summary.TotalTests, summary.Passed, summary.Failed, summary.Skipped));
        Assert.Equal(45, summary.TotalDurationMs);

        await port.AddCoverageResultAsync(new CoverageResult { PipelineRunId = 10, LineRate = 0.8, BranchRate = 0.6, CreatedAt = At }, Ct);
        Assert.Equal(0.8, Assert.Single(await port.GetCoverageResultsAsync(10, Ct)).LineRate);
        Assert.Empty(await port.GetCoverageResultsAsync(11, Ct));

        await port.AddLintResultAsync(new LintResult { PipelineRunId = 10, Tool = "eslint", ErrorCount = 2, CreatedAt = At }, Ct);
        Assert.Equal(2, Assert.Single(await port.GetLintResultsAsync(10, Ct)).ErrorCount);

        await port.AddRunMetricsAsync(
            [new RunMetric { PipelineRunId = 11, Key = "loc.total", Type = RunMetricType.Count, Value = 1200, CreatedAt = At }], Ct);
        Assert.Equal(1200, Assert.Single(await port.GetRunMetricsAsync(11, Ct)).Value);
        Assert.Empty(await port.GetRunMetricsAsync(10, Ct));
    }

    [Fact]
    public async Task TheArtifactPortAndTheRunLookups_AnswerForKnownAndUnknownRows()
    {
        await SeedAsync();
        IPipelineArtifactRepository artifacts = new PipelineArtifactRepository(_db);
        IPipelineRunLookupReader runs = new PipelineRunLookupRepository(_db);

        var later = new PipelineArtifact { Id = 4, PipelineRunId = 10, PipelineId = 1, Name = "later.zip" };
        var earlier = new PipelineArtifact { Id = 5, PipelineRunId = 10, PipelineId = 1, Name = "earlier.zip" };
        await artifacts.AddArtifactAsync(later, Ct);
        await artifacts.AddArtifactAsync(earlier, Ct);
        // The context stamps CreatedAt on insert; set distinct times afterwards so the order is decided
        // by CreatedAt alone, against the id order.
        later.CreatedAt = At.AddMinutes(2);
        earlier.CreatedAt = At;
        await _db.SaveChangesAsync(Ct);
        Assert.Equal(["earlier.zip", "later.zip"], (await artifacts.GetArtifactsAsync(10, Ct)).Select(a => a.Name));
        Assert.Equal(1, await artifacts.GetPipelineIdForArtifactAsync(5, Ct));
        Assert.Null(await artifacts.GetPipelineIdForArtifactAsync(404, Ct));

        Assert.Equal(1, await runs.GetPipelineIdForRunAsync(11, Ct));
        Assert.Null(await runs.GetPipelineIdForRunAsync(404, Ct));
        var statuses = await runs.GetRunStatusesByIdsAsync([10, 11, 404], Ct);
        Assert.Equal(2, statuses.Count);
        Assert.Equal(PipelineStatus.Success, statuses[10]);
        Assert.Equal(PipelineStatus.Running, statuses[11]);
    }

    [Fact]
    public async Task TheFacade_AnswersExactlyWhatThePortsAnswer()
    {
        await SeedAsync();
        _db.TestResults.Add(new TestResult { PipelineRunId = 10, TestSuite = "A", TestName = "t", Outcome = TestOutcome.Passed, DurationMs = 7 });
        _db.CoverageResults.Add(new CoverageResult { PipelineRunId = 10, LineRate = 0.5, CreatedAt = At });
        _db.PipelineArtifacts.Add(new PipelineArtifact { Id = 4, PipelineRunId = 10, PipelineId = 1, Name = "a.zip", CreatedAt = At });
        await _db.SaveChangesAsync(Ct);
        IPipelineQualityResultRepository quality = new PipelineCoverageRepository(_db);
        IPipelineArtifactRepository artifacts = new PipelineArtifactRepository(_db);
        IPipelineRunLookupReader runs = new PipelineRunLookupRepository(_db);
        var facade = Facade();

        Assert.Equal(
            (await quality.GetTestResultsAsync(10, Ct)).Select(t => t.Id),
            (await facade.GetTestResultsAsync(10, Ct)).Select(t => t.Id));
        Assert.Equal(
            (await quality.GetTestResultSummaryAsync(10, Ct))?.TotalTests,
            (await facade.GetTestResultSummaryAsync(10, Ct))?.TotalTests);
        Assert.Equal(
            (await quality.GetCoverageResultsAsync(10, Ct)).Select(c => c.Id),
            (await facade.GetCoverageResultsAsync(10, Ct)).Select(c => c.Id));
        Assert.Equal(
            (await artifacts.GetArtifactsAsync(10, Ct)).Select(a => a.Id),
            (await facade.GetArtifactsAsync(10, Ct)).Select(a => a.Id));
        Assert.Equal(
            await artifacts.GetPipelineIdForArtifactAsync(4, Ct),
            await facade.GetPipelineIdForArtifactAsync(4, Ct));
        Assert.Equal(
            await runs.GetRunStatusesByIdsAsync([10, 11], Ct),
            await facade.GetRunStatusesByIdsAsync([10, 11], Ct));
    }

    public void Dispose() => _db.Dispose();
}
