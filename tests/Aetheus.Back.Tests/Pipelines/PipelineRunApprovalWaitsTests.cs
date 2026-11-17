// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-33: the time a finished run spent waiting for approvals, which the "average duration" tile leaves out.
/// Overlapping waits count once, an unresolved wait on a finished run lasts until the run completed.</summary>
public sealed class PipelineRunApprovalWaitsTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _db.Dispose();

    private static DateTime At(int minutes) => Start.AddMinutes(minutes);

    [Fact]
    public void Total_WithoutApprovals_IsZero() =>
        Assert.Equal(TimeSpan.Zero, PipelineRunApprovalWaits.Total([], At(0), At(120)));

    [Fact]
    public void Total_AddsDisjointWaits() =>
        Assert.Equal(
            TimeSpan.FromMinutes(40),
            PipelineRunApprovalWaits.Total([(At(10), At(30)), (At(50), At(70))], At(0), At(120)));

    [Fact]
    public void Total_CountsOverlappingWaitsOnce() =>
        Assert.Equal(
            TimeSpan.FromMinutes(50),
            PipelineRunApprovalWaits.Total([(At(10), At(40)), (At(30), At(60)), (At(20), At(35))], At(0), At(120)));

    [Fact]
    public void Total_AWaitInsideAnotherAddsNothing() =>
        Assert.Equal(
            TimeSpan.FromMinutes(60),
            PipelineRunApprovalWaits.Total([(At(0), At(60)), (At(10), At(20))], At(0), At(120)));

    [Fact]
    public void Total_AnUnresolvedWaitLastsUntilTheRunCompleted() =>
        Assert.Equal(
            TimeSpan.FromMinutes(90),
            PipelineRunApprovalWaits.Total([(At(30), null)], At(0), At(120)));

    [Fact]
    public void Total_ClampsAWaitToTheRunWindow() =>
        Assert.Equal(
            TimeSpan.FromMinutes(20),
            PipelineRunApprovalWaits.Total([(At(-30), At(10)), (At(100), At(500))], At(0), At(110)));

    [Fact]
    public async Task ApplyAsync_FillsTheWaitOfFinishedRunsOnly()
    {
        _db.Pipelines.Add(new Pipeline { Id = 1, Name = "deploy" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 1, PipelineId = 1, StartedAt = At(0), CompletedAt = At(120) },
            new PipelineRun { Id = 2, PipelineId = 1, StartedAt = At(0), CompletedAt = At(120) },
            new PipelineRun { Id = 3, PipelineId = 1, StartedAt = At(0), CompletedAt = null });
        _db.PipelineApprovals.AddRange(
            new PipelineApproval { PipelineRunId = 1, RequestedAt = At(10), ResolvedAt = At(70) },
            new PipelineApproval { PipelineRunId = 1, RequestedAt = At(30), ResolvedAt = At(80) },
            new PipelineApproval { PipelineRunId = 2, RequestedAt = At(60), ResolvedAt = null },
            new PipelineApproval { PipelineRunId = 3, RequestedAt = At(5), ResolvedAt = null });
        await _db.SaveChangesAsync(Ct);
        List<PipelineRunDto> runs =
        [
            new() { Id = 1, StartedAt = At(0), CompletedAt = At(120) },
            new() { Id = 2, StartedAt = At(0), CompletedAt = At(120) },
            new() { Id = 3, StartedAt = At(0) },
            new() { Id = 4, StartedAt = At(0), CompletedAt = At(10) }
        ];

        var result = await PipelineRunApprovalWaits.ApplyAsync(_db, runs, Ct);

        Assert.Equal(70 * 60, result.Single(run => run.Id == 1).ApprovalWaitSeconds);
        Assert.Equal(60 * 60, result.Single(run => run.Id == 2).ApprovalWaitSeconds);
        Assert.Equal(0, result.Single(run => run.Id == 3).ApprovalWaitSeconds);
        Assert.Equal(0, result.Single(run => run.Id == 4).ApprovalWaitSeconds);
    }
}
