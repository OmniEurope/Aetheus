// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Analysis.DependencyBump;
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
/// R-6: the end of the cve-lock-refresh run of an automatic update. Real EF rows (in memory); the git write, the artifact and the
/// launches are doubles, so nothing here proves a real commit nor a real run: it proves what is committed where, the owner checked
/// again, and every way the update fails with its reason.
/// </summary>
public sealed class DependencyUpdateLockFollowUpTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);
    private const string Branch = "aetheus/deps/lodash-4.17.21";
    private const int LockRunId = 4000;
    private const string Lock = """{"lockfileVersion":3,"packages":{"":{"name":"app"},"node_modules/lodash":{"version":"4.17.21"}}}""";

    private readonly AppDbContext _db;
    private readonly FakeTimeProvider _time = new(Now);
    private readonly Git _git = new();
    private readonly LockRefresh _lockRefresh = new();
    private readonly Pipelines _pipelines = new();
    private readonly DependencyUpdateLockFollowUp _followUp;

    public DependencyUpdateLockFollowUpTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            _time);
        var repository = new DependencyUpdateRepository(_db);
        _followUp = new DependencyUpdateLockFollowUp(repository, _git, _lockRefresh,
            new DependencyUpdateCiLaunch(repository, _pipelines, _time), _time,
            NullLogger<DependencyUpdateLockFollowUp>.Instance);
        _db.Pipelines.AddRange(
            new Pipeline { Id = 11, ProjectId = 3, Name = "app-ci", TemplateReferenceName = "application-ci" },
            new Pipeline { Id = 14, ProjectId = 3, Name = "app-qa", TemplateReferenceName = "application-qa" });
        _db.DependencyUpdates.Add(new DependencyUpdate
        {
            Id = 5,
            OrganizationId = 2,
            ProjectId = 3,
            GitInternalRepoId = 7,
            Ecosystem = "npm",
            PackageName = "lodash",
            FromVersion = "4.17.1",
            TargetVersion = "4.17.21",
            Advisory = "GHSA-xxxx",
            Status = DependencyUpdateStatus.LockRefreshing,
            Branch = Branch,
            CommitSha = "manifest1",
            LockPipelineId = 40,
            LockPipelineRunId = LockRunId,
            LockRefreshOwner = "owner",
            LockFilesJson = "[\"package-lock.json\"]",
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
    public async Task RunSucceeded_CommitsTheLockFilesOnTheUpdateBranchThenLaunchesTheCiAtThatCommit()
    {
        await CompleteAsync(LockRunId, PipelineStatus.Success);

        var write = Assert.Single(_git.Writes);
        // On the update's own branch, on top of the commit that holds the bumped manifest: never the default branch.
        Assert.Equal((Branch, "manifest1"), (write.Branch, write.BaseCommit));
        Assert.Equal([("package-lock.json", Lock)], write.Files);
        Assert.Contains("launched as its owner 'owner'", write.Message, StringComparison.Ordinal);
        Assert.Equal([(40, "owner")], _lockRefresh.Rechecks);
        var row = Row();
        Assert.Equal((DependencyUpdateStatus.BranchPushed, "locks2"), (row.Status, row.CommitSha));
        Assert.Equal([(11, Branch, "locks2", 5)], _pipelines.Launches);
        Assert.Equal((11, 1100, 14), (row.CiPipelineId, row.CiPipelineRunId, row.QaPipelineId));
        Assert.Null(row.Reason);
    }

    [Fact]
    public async Task RunFailed_FailsTheUpdateWithItsReasonAndCommitsNothing()
    {
        await CompleteAsync(LockRunId, PipelineStatus.Failed);

        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Failed, row.Status);
        Assert.Contains($"the run {LockRunId} of the lock refresh ended as Failed", row.Reason, StringComparison.Ordinal);
        Assert.Empty(_git.Writes);
        Assert.Empty(_pipelines.Launches);
    }

    [Fact]
    public async Task OwnerNoLongerAllowedWhenTheRunEnds_FailsTheUpdateAndCommitsNothing()
    {
        _lockRefresh.Refusal = "The owner 'owner' of the pipeline 'cve-lock-refresh' is disabled, and the lock refresh runs under no other identity.";

        await CompleteAsync(LockRunId, PipelineStatus.Success);

        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Failed, row.Status);
        Assert.Contains("is disabled", row.Reason, StringComparison.Ordinal);
        Assert.Empty(_git.Writes);
        Assert.Empty(_pipelines.Launches);
    }

    [Fact]
    public async Task LockFileBelowTheTarget_FailsTheUpdateAndCommitsNothing()
    {
        _lockRefresh.Locks = new CveRemediationLocks(
            [("package-lock.json", """{"lockfileVersion":3,"packages":{"node_modules/lodash":{"version":"4.17.15"}}}""")], null);

        await CompleteAsync(LockRunId, PipelineStatus.Success);

        Assert.Equal(DependencyUpdateStatus.Failed, Row().Status);
        Assert.Empty(_git.Writes);
    }

    [Fact]
    public async Task ArtifactMissing_FailsTheUpdateWithTheReason()
    {
        _lockRefresh.Locks = new CveRemediationLocks([], "The run published no artifact named 'dependency-locks'.");

        await CompleteAsync(LockRunId, PipelineStatus.Success);

        Assert.Contains("no artifact named 'dependency-locks'", Row().Reason, StringComparison.Ordinal);
        Assert.Empty(_git.Writes);
    }

    [Fact]
    public async Task AnotherRun_IsIgnored()
    {
        await CompleteAsync(9999, PipelineStatus.Success);

        Assert.Equal(DependencyUpdateStatus.LockRefreshing, Row().Status);
        Assert.Empty(_git.Writes);
        Assert.Empty(_lockRefresh.Rechecks);
    }

    private Task ResumeAsync() => _followUp.ResumeAsync(TestContext.Current.CancellationToken);

    private async Task SeedLockRunAsync(PipelineStatus status)
    {
        _db.PipelineRuns.Add(new PipelineRun { Id = LockRunId, PipelineId = 40, Status = status, StartedAt = Now.UtcDateTime });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Resume_RunSucceededButItsEventWasLost_CommitsTheLockFilesAndLaunchesTheCiAsTheEventWould()
    {
        await SeedLockRunAsync(PipelineStatus.Success);

        await ResumeAsync();

        var write = Assert.Single(_git.Writes);
        Assert.Equal((Branch, "manifest1"), (write.Branch, write.BaseCommit));
        var row = Row();
        Assert.Equal((DependencyUpdateStatus.BranchPushed, "locks2"), (row.Status, row.CommitSha));
        Assert.Equal([(11, Branch, "locks2", 5)], _pipelines.Launches);
    }

    [Fact]
    public async Task Resume_RunFailedButItsEventWasLost_FailsTheUpdateWithTheRealStatus()
    {
        await SeedLockRunAsync(PipelineStatus.Failed);

        await ResumeAsync();

        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Failed, row.Status);
        Assert.Contains("ended as Failed", row.Reason, StringComparison.Ordinal);
        Assert.Empty(_git.Writes);
    }

    [Fact]
    public async Task Resume_RunStillGoing_IsLeftAlone()
    {
        await SeedLockRunAsync(PipelineStatus.Running);
        _time.Advance(TimeSpan.FromDays(2));

        await ResumeAsync();

        Assert.Equal(DependencyUpdateStatus.LockRefreshing, Row().Status);
        Assert.Empty(_git.Writes);
    }

    [Fact]
    public async Task Resume_RunNotFound_FailsTheUpdateOnceTheGracePassed_NotBefore()
    {
        await ResumeAsync();
        Assert.Equal(DependencyUpdateStatus.LockRefreshing, Row().Status);

        _time.Advance(TimeSpan.FromMinutes(20));
        await ResumeAsync();

        var row = Row();
        Assert.Equal(DependencyUpdateStatus.Failed, row.Status);
        Assert.Contains($"the lock refresh run {LockRunId} no longer exists", row.Reason, StringComparison.Ordinal);
        Assert.Empty(_git.Writes);
        Assert.Empty(_pipelines.Launches);
    }

    [Fact]
    public async Task Resume_RowThatNeverRecordedItsRun_FailsOnceTheGracePassed()
    {
        var update = await _db.DependencyUpdates.SingleAsync(TestContext.Current.CancellationToken);
        update.LockPipelineRunId = null;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(20));

        await ResumeAsync();

        Assert.Contains("no lock refresh run was recorded", Row().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_UpdateNotWaitingForALockRefresh_IsNotTouched()
    {
        await SeedLockRunAsync(PipelineStatus.Success);
        var update = await _db.DependencyUpdates.SingleAsync(TestContext.Current.CancellationToken);
        update.Status = DependencyUpdateStatus.BranchPushed;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await ResumeAsync();

        Assert.Empty(_git.Writes);
        Assert.Equal(DependencyUpdateStatus.BranchPushed, Row().Status);
    }

    [Fact]
    public async Task Resume_AfterTheEventWasHandled_CommitsOnlyOnce()
    {
        await SeedLockRunAsync(PipelineStatus.Success);
        await CompleteAsync(LockRunId, PipelineStatus.Success);

        await ResumeAsync();

        Assert.Single(_git.Writes);
    }

    [Fact]
    public async Task RunEndedTwice_CommitsOnce()
    {
        await CompleteAsync(LockRunId, PipelineStatus.Success);
        await CompleteAsync(LockRunId, PipelineStatus.Success);

        Assert.Single(_git.Writes);
        Assert.Single(_pipelines.Launches);
    }

    private sealed class Git : IDependencyUpdateGit
    {
        private readonly GitInternalRepo _repo = new() { Id = 7, ProjectId = 3, Name = "app", Slug = "app", DefaultBranch = "main" };

        public List<(string Branch, string BaseCommit, IReadOnlyList<(string Path, string Content)> Files, string Message)> Writes { get; } = [];

        public Task<CveRemediationRepositoryRow?> FindRepositoryAsync(int repositoryId, int projectId, CancellationToken ct) =>
            Task.FromResult<CveRemediationRepositoryRow?>(new CveRemediationRepositoryRow(_repo, false));

        public Task<CveRemediationPlan> PlanAsync(int repositoryId, string baseBranch, DependencyBumpRequest request, CancellationToken ct) =>
            throw new NotSupportedException("The follow-up never plans a bump.");

        public Task<DependencyUpdateWrite> WriteAsync(
            GitInternalRepo repository, string branch, string baseCommit, IReadOnlyList<(string Path, string Content)> files, string message,
            CancellationToken ct)
        {
            Writes.Add((branch, baseCommit, files, message));
            return Task.FromResult(new DependencyUpdateWrite("locks2", null));
        }
    }

    private sealed class LockRefresh : IDependencyUpdateLockRefresh
    {
        public string? Refusal { get; set; }

        public CveRemediationLocks Locks { get; set; } = new([("package-lock.json", Lock)], null);

        public List<(int PipelineId, string Owner)> Rechecks { get; } = [];

        public Task<DependencyUpdateLockReadiness> CheckAsync(int projectId, string baseBranch, CancellationToken ct) =>
            throw new NotSupportedException("The follow-up never checks a new refresh.");

        public Task<string?> RecheckAsync(int pipelineId, string owner, CancellationToken ct)
        {
            Rechecks.Add((pipelineId, owner));
            return Task.FromResult(Refusal);
        }

        public Task<DependencyUpdateLaunch> LaunchAsync(DependencyUpdateLockLaunch launch, CancellationToken ct) =>
            throw new NotSupportedException("The follow-up never launches a refresh.");

        public Task<CveRemediationLocks> ReadLocksAsync(int runId, IReadOnlyList<string> paths, CancellationToken ct) => Task.FromResult(Locks);
    }

    private sealed class Pipelines : IDependencyUpdatePipelines
    {
        public List<(int PipelineId, string Branch, string Commit, int UpdateId)> Launches { get; } = [];

        public Task<DependencyUpdateLaunch> LaunchAsync(int pipelineId, string branch, string commit, int updateId, CancellationToken ct)
        {
            Launches.Add((pipelineId, branch, commit, updateId));
            return Task.FromResult(new DependencyUpdateLaunch(pipelineId * 100, null));
        }
    }
}
