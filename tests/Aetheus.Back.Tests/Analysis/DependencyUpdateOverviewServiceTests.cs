// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis.DependencyUpdates;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.DependencyUpdates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// R-2 (option B): what the Quality page reads of a major dependency update: flagged as a major, and as needing the user's review only
/// once its branch exists and it was not refused. Real EF rows (in memory).
/// </summary>
public sealed class DependencyUpdateOverviewServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly AppDbContext _db;
    private readonly DependencyUpdateOverviewService _service;

    public DependencyUpdateOverviewServiceTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new FakeTimeProvider(Now));
        _service = new DependencyUpdateOverviewService(new DependencyUpdateRepository(_db), Substitute.For<IAuditService>(), new FakeTimeProvider(Now));
        _db.Projects.Add(new Project { Id = 3, OrganizationId = 2, Name = "app" });
        Add(1, "patch", "1.0.0", "1.0.1", major: false, DependencyUpdateStatus.BranchPushed, "aetheus/deps/patch-1.0.1");
        Add(2, "big", "1.0.0", "2.0.0", major: true, DependencyUpdateStatus.BranchPushed, "aetheus/deps/big-2.0.0");
        Add(3, "refused", "1.0.0", "3.0.0", major: true, DependencyUpdateStatus.Refused, null);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private void Add(int id, string package, string from, string target, bool major, DependencyUpdateStatus status, string? branch) =>
        _db.DependencyUpdates.Add(new DependencyUpdate
        {
            Id = id,
            OrganizationId = 2,
            ProjectId = 3,
            GitInternalRepoId = 7,
            Ecosystem = "NuGet",
            PackageName = package,
            FromVersion = from,
            TargetVersion = target,
            Advisory = "GHSA-xxxx",
            IsMajorUpgrade = major,
            Status = status,
            Branch = branch,
            CommitSha = branch is null ? null : "abc123",
            CreatedAt = Now.UtcDateTime,
            UpdatedAt = Now.UtcDateTime
        });

    [Fact]
    public async Task Get_ReadsTheLatestRunOfEachPipelineOnTheExactBranchAndCommit_InOneGroupedLookup()
    {
        _db.Pipelines.AddRange(new Pipeline { Id = 11, ProjectId = 3, Name = "app-ci" }, new Pipeline { Id = 14, ProjectId = 3, Name = "app-qa" });
        foreach (var update in _db.DependencyUpdates.Where(update => update.Branch != null))
        {
            update.CiPipelineId = 11;
            update.QaPipelineId = 14;
            update.CiPipelineRunId = 1;
        }

        PipelineRun Run(int id, int pipeline, string branch, string commit, PipelineStatus status) =>
            new() { Id = id, PipelineId = pipeline, BranchName = branch, CommitHash = commit, Status = status, StartedAt = Now.UtcDateTime };
        _db.PipelineRuns.AddRange(
            Run(100, 11, "aetheus/deps/patch-1.0.1", "abc123", PipelineStatus.Failed),
            Run(101, 11, "aetheus/deps/patch-1.0.1", "abc123", PipelineStatus.Success),
            Run(102, 14, "aetheus/deps/patch-1.0.1", "abc123", PipelineStatus.Running),
            // The other branch's CI, and the same branch at another commit: neither is the run asked for.
            Run(103, 11, "aetheus/deps/big-2.0.0", "abc123", PipelineStatus.Cancelled),
            Run(104, 11, "aetheus/deps/patch-1.0.1", "other", PipelineStatus.Partial));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var overview = await _service.GetAsync(3, TestContext.Current.CancellationToken);

        var items = Assert.IsType<DependencyUpdatesOverviewDto>(overview).Items.ToDictionary(item => item.PackageName);
        Assert.Equal([(11, 101, PipelineStatus.Success), (14, 102, PipelineStatus.Running)],
            items["patch"].Runs.Select(run => (run.PipelineId, run.RunId!.Value, run.Status!.Value)));
        // big: its own CI run 103 on its branch; no QA run exists for it, and the update recorded none either.
        Assert.Equal((103, PipelineStatus.Cancelled), (items["big"].Runs.First().RunId!.Value, items["big"].Runs.First().Status!.Value));
        Assert.Null(items["big"].Runs.Last().Status);
    }

    [Fact]
    public async Task Get_FlagsAMajorUpdateAndRequiresItsReviewOnlyOnceItsBranchExists()
    {
        var overview = await _service.GetAsync(3, TestContext.Current.CancellationToken);

        var byName = Assert.IsType<DependencyUpdatesOverviewDto>(overview).Items.ToDictionary(item => item.PackageName);
        Assert.Equal((false, false), (byName["patch"].IsMajorUpgrade, byName["patch"].ReviewRequired));
        Assert.Equal((true, true), (byName["big"].IsMajorUpgrade, byName["big"].ReviewRequired));
        Assert.Equal((true, false), (byName["refused"].IsMajorUpgrade, byName["refused"].ReviewRequired));
    }
}
