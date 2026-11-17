// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis.DependencyUpdates;
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.DependencyUpdates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// PLAN-007: the QA of an automatic dependency update starts once, and only when the CI run of that update succeeded (the QA restores
/// that run's artifacts). Real EF rows (in memory); the launch is a double, so nothing here proves a real QA run.
/// </summary>
public sealed class DependencyUpdateQaFollowUpTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 5, 0, 0, TimeSpan.Zero);
    private const string Branch = "aetheus/deps/Newtonsoft.Json-13.0.3";
    private const int CiRunId = 1100;

    private readonly AppDbContext _db;
    private readonly Launcher _launcher = new();
    private readonly DependencyUpdateQaFollowUp _followUp;

    public DependencyUpdateQaFollowUpTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new FakeTimeProvider(Now));
        _followUp = new DependencyUpdateQaFollowUp(new DependencyUpdateRepository(_db), _launcher, new FakeTimeProvider(Now),
            NullLogger<DependencyUpdateQaFollowUp>.Instance);
        _db.DependencyUpdates.Add(new DependencyUpdate
        {
            Id = 5,
            OrganizationId = 2,
            ProjectId = 3,
            GitInternalRepoId = 7,
            Ecosystem = "NuGet",
            PackageName = "Newtonsoft.Json",
            FromVersion = "13.0.1",
            TargetVersion = "13.0.3",
            Advisory = "GHSA-xxxx",
            Status = DependencyUpdateStatus.BranchPushed,
            Branch = Branch,
            CommitSha = "abc123",
            CiPipelineId = 11,
            CiPipelineRunId = CiRunId,
            QaPipelineId = 14,
            CreatedAt = Now.UtcDateTime,
            UpdatedAt = Now.UtcDateTime
        });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private DependencyUpdate Row() => Assert.Single(_db.DependencyUpdates.AsNoTracking());

    private Task CompleteAsync(int runId, PipelineStatus status) =>
        _followUp.HandleAsync(new PipelineRunCompletedEvent(runId, status), TestContext.Current.CancellationToken);

    [Fact]
    public async Task CiSucceeded_LaunchesTheQaOnTheBranchAndCommitOfTheUpdate()
    {
        await CompleteAsync(CiRunId, PipelineStatus.Success);

        Assert.Equal([(14, Branch, "abc123", 5)], _launcher.Launches);
        var row = Row();
        Assert.Equal(1400, row.QaPipelineRunId);
        Assert.Null(row.Reason);
    }

    [Fact]
    public async Task CiOfAMajorUpdateSucceeded_LaunchesTheQaOnlyAndNeverACandidateOrADeployment()
    {
        var tracked = await _db.DependencyUpdates.SingleAsync(TestContext.Current.CancellationToken);
        tracked.IsMajorUpgrade = true;
        tracked.TargetVersion = "14.0.0";
        tracked.Branch = "aetheus/deps/Newtonsoft.Json-14.0.0";
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await CompleteAsync(CiRunId, PipelineStatus.Success);

        Assert.Equal([(14, "aetheus/deps/Newtonsoft.Json-14.0.0", "abc123", 5)], _launcher.Launches);
        Assert.Equal(DependencyUpdateStatus.BranchPushed, Row().Status);
    }

    [Fact]
    public async Task CiSucceededTwice_LaunchesTheQaOnce()
    {
        await CompleteAsync(CiRunId, PipelineStatus.Success);
        await CompleteAsync(CiRunId, PipelineStatus.Success);

        Assert.Single(_launcher.Launches);
    }

    [Theory]
    [InlineData(PipelineStatus.Failed)]
    [InlineData(PipelineStatus.Cancelled)]
    [InlineData(PipelineStatus.Partial)]
    [InlineData(PipelineStatus.Rejected)]
    public async Task CiDidNotSucceed_StartsNoQaAndRecordsTheReason(PipelineStatus status)
    {
        await CompleteAsync(CiRunId, status);

        Assert.Empty(_launcher.Launches);
        var row = Row();
        Assert.Null(row.QaPipelineRunId);
        Assert.Contains($"QA not started: the CI run {CiRunId} ended as {status}.", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QaLaunchRefused_RecordsTheReasonAndKeepsNoRun()
    {
        _launcher.Refusal = "the pipeline is disabled";

        await CompleteAsync(CiRunId, PipelineStatus.Success);

        var row = Row();
        Assert.Null(row.QaPipelineRunId);
        Assert.Contains("QA not started: the pipeline is disabled", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOfAnotherPipelineOrUpdate_IsIgnored()
    {
        await CompleteAsync(9999, PipelineStatus.Success);
        await CompleteAsync(9999, PipelineStatus.Failed);

        Assert.Empty(_launcher.Launches);
        Assert.Null(Row().Reason);
    }

    [Fact]
    public async Task UpdateWithoutQaPipeline_LaunchesNothing()
    {
        var tracked = await _db.DependencyUpdates.SingleAsync(TestContext.Current.CancellationToken);
        tracked.QaPipelineId = null;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await CompleteAsync(CiRunId, PipelineStatus.Success);

        Assert.Empty(_launcher.Launches);
    }

    private sealed class Launcher : IDependencyUpdatePipelines
    {
        public string? Refusal { get; set; }

        public List<(int PipelineId, string Branch, string Commit, int UpdateId)> Launches { get; } = [];

        public Task<DependencyUpdateLaunch> LaunchAsync(int pipelineId, string branch, string commit, int updateId, CancellationToken ct)
        {
            Launches.Add((pipelineId, branch, commit, updateId));
            return Task.FromResult(Refusal is null ? new DependencyUpdateLaunch(pipelineId * 100, null) : new DependencyUpdateLaunch(null, Refusal));
        }
    }
}
