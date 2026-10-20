// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Releases;

/// <summary>
/// Recette R1-72, decision D2: which releases the 30-day retention archives, and what archiving deletes. Every protection is pinned by a release
/// that would otherwise be archived; a newer release of the same project always exists, so the
/// newest-of-project protection does not mask the others.
/// </summary>
public sealed class ReleaseRetentionRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Cutoff = Now.AddDays(-30);
    private static readonly DateTime July = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db;
    private readonly ReleaseRetentionRepository _repository;
    private int _nextId = 1;

    public ReleaseRetentionRepositoryTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _repository = new ReleaseRetentionRepository(_db);
        _db.Pipelines.Add(new Pipeline { Id = 1, Name = "CI" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 1, PipelineId = 1, Status = PipelineStatus.Success, StartedAt = July },
            new PipelineRun { Id = 2, PipelineId = 1, Status = PipelineStatus.Running, StartedAt = July });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private Release Add(Release release, int projectId = 1)
    {
        release.Id = _nextId++;
        release.ProjectId = projectId;
        release.Version = $"1.0.{release.Id}";
        _db.Releases.Add(release);
        return release;
    }

    /// <summary>Recent and added last, so no old release is its project's newest.</summary>
    private void AddNewest() =>
        Add(new Release { Status = ReleaseStatus.Published, DetectedAt = Now, PublishedAt = Now.AddDays(-1) });

    private async Task<List<int>> ExpiredAsync()
    {
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return await _repository.GetExpiredReleaseIdsAsync(Cutoff, Now, 100, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnOldPublishedRelease_IsExpired_AndARecentOneIsNot()
    {
        var old = Add(new Release { Status = ReleaseStatus.Published, DetectedAt = July, PublishedAt = July });
        var recent = Add(new Release { Status = ReleaseStatus.Published, DetectedAt = Now.AddDays(-29), PublishedAt = Now.AddDays(-29) });
        AddNewest();

        var expired = await ExpiredAsync();

        Assert.Contains(old.Id, expired);
        Assert.DoesNotContain(recent.Id, expired);
    }

    [Fact]
    public async Task AReleaseWithoutDetectionDate_TakesItsPublicationDate()
    {
        var undated = Add(new Release { Status = ReleaseStatus.Published, PublishedAt = July });
        AddNewest();

        Assert.Contains(undated.Id, await ExpiredAsync());
    }

    [Fact]
    public async Task AReleaseWithoutDetectionOrPublicationDate_TakesTheStartOfItsRun()
    {
        var byPublishingRun = Add(new Release { Status = ReleaseStatus.Failed, CreatedByPipelineRunId = 1 });
        var byRecordingRun = Add(new Release { Status = ReleaseStatus.Failed, PipelineRunId = 1 });
        AddNewest();

        var expired = await ExpiredAsync();

        Assert.Contains(byPublishingRun.Id, expired);
        Assert.Contains(byRecordingRun.Id, expired);
    }

    [Fact]
    public async Task AReleaseWithNoDateAtAll_IsKept()
    {
        var undatable = Add(new Release { Status = ReleaseStatus.Published });
        AddNewest();

        Assert.DoesNotContain(undatable.Id, await ExpiredAsync());
    }

    [Fact]
    public async Task TheDeployedRelease_IsKept()
    {
        var deployed = Add(new Release { Status = ReleaseStatus.Deployed, DetectedAt = July, PublishedAt = July });
        AddNewest();

        Assert.DoesNotContain(deployed.Id, await ExpiredAsync());
    }

    [Fact]
    public async Task TheLastThreeDeployedReleases_AreKept_AndTheFourthIsNot()
    {
        var superseded = Enumerable.Range(0, 4)
            .Select(index => Add(new Release
            {
                Status = ReleaseStatus.Superseded,
                DetectedAt = July.AddDays(index),
                PublishedAt = July.AddDays(index)
            }))
            .ToList();
        AddNewest();

        var expired = await ExpiredAsync();

        Assert.Contains(superseded[0].Id, expired);
        Assert.DoesNotContain(superseded[1].Id, expired);
        Assert.DoesNotContain(superseded[2].Id, expired);
        Assert.DoesNotContain(superseded[3].Id, expired);
    }

    [Fact]
    public async Task AReleaseWhoseArtifactIsLeasedOrAwaitsDependencyTrack_IsKept()
    {
        var leasedArtifact = new PipelineArtifact { Id = 1, PipelineId = 1, PipelineRunId = 1, ProjectId = 1, RetentionLeaseExpiresAt = Now.AddHours(1) };
        var deliveringArtifact = new PipelineArtifact { Id = 2, PipelineId = 1, PipelineRunId = 1, ProjectId = 1 };
        var expiredLeaseArtifact = new PipelineArtifact { Id = 3, PipelineId = 1, PipelineRunId = 1, ProjectId = 1, RetentionLeaseExpiresAt = Now.AddHours(-1) };
        _db.DependencyTrackOutboxItems.Add(new DependencyTrackOutboxItem { Id = 1, PipelineArtifactId = 2, ProjectId = 1 });
        var leased = Add(new Release { Status = ReleaseStatus.Published, PublishedAt = July, Artifacts = [leasedArtifact] });
        var delivering = Add(new Release { Status = ReleaseStatus.Published, PublishedAt = July, Artifacts = [deliveringArtifact] });
        var leaseOver = Add(new Release { Status = ReleaseStatus.Published, PublishedAt = July, Artifacts = [expiredLeaseArtifact] });
        AddNewest();

        var expired = await ExpiredAsync();

        Assert.DoesNotContain(leased.Id, expired);
        Assert.DoesNotContain(delivering.Id, expired);
        Assert.Contains(leaseOver.Id, expired);
    }

    [Fact]
    public async Task AReleaseOfARunInProgress_IsKept()
    {
        var building = Add(new Release { Status = ReleaseStatus.Building, PublishedAt = July, CreatedByPipelineRunId = 2 });
        AddNewest();

        Assert.DoesNotContain(building.Id, await ExpiredAsync());
    }

    [Fact]
    public async Task AReleaseNamedByARollback_IsKept()
    {
        var source = Add(new Release { Status = ReleaseStatus.Published, PublishedAt = July });
        var target = Add(new Release { Status = ReleaseStatus.Published, PublishedAt = July });
        _db.ReleaseRollbacks.Add(new ReleaseRollback { Id = 1, SourceReleaseId = source.Id, TargetReleaseId = target.Id, PipelineId = 1 });
        AddNewest();

        var expired = await ExpiredAsync();

        Assert.DoesNotContain(source.Id, expired);
        Assert.DoesNotContain(target.Id, expired);
    }

    [Fact]
    public async Task TheNewestReleaseOfAProject_IsKeptHoweverOld()
    {
        var onlyRelease = Add(new Release { Status = ReleaseStatus.Published, PublishedAt = July }, projectId: 2);
        var older = Add(new Release { Status = ReleaseStatus.Published, PublishedAt = July }, projectId: 3);
        var newestOfThree = Add(new Release { Status = ReleaseStatus.Published, PublishedAt = July }, projectId: 3);

        var expired = await ExpiredAsync();

        Assert.DoesNotContain(onlyRelease.Id, expired);
        Assert.Contains(older.Id, expired);
        Assert.DoesNotContain(newestOfThree.Id, expired);
    }

    [Fact]
    public async Task ArchiveExpiredAsync_ChecksAgain_KeepsTheReleaseAndItsCommit_AndIsIdempotent()
    {
        var expiring = Add(new Release { Status = ReleaseStatus.Published, PublishedAt = July, CommitHash = new string('a', 40), BranchName = "main" });
        var deployedMeanwhile = Add(new Release { Status = ReleaseStatus.Published, PublishedAt = July });
        AddNewest();
        var candidates = await ExpiredAsync();
        Assert.Equal([expiring.Id, deployedMeanwhile.Id], candidates);

        deployedMeanwhile.Status = ReleaseStatus.Deployed;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(1, await _repository.ArchiveExpiredAsync(candidates, Cutoff, Now, ct));
        Assert.Equal(0, await _repository.ArchiveExpiredAsync(candidates, Cutoff, Now, ct));
        var archived = await _db.Releases.AsNoTracking().SingleAsync(release => release.Id == expiring.Id, ct);
        Assert.Equal(ReleaseStatus.Archived, archived.Status);
        Assert.Equal(Now, archived.ArchivedAt);
        Assert.Equal(new string('a', 40), archived.CommitHash);
        Assert.Equal("main", archived.BranchName);
        Assert.Equal(ReleaseStatus.Deployed, (await _db.Releases.AsNoTracking().SingleAsync(release => release.Id == deployedMeanwhile.Id, ct)).Status);
        Assert.DoesNotContain(expiring.Id, await ExpiredAsync());
    }

    [Fact]
    public async Task Archiving_DeletesTheArtifactsAndTheRunOfTheRelease_ButNotWhatALiveReleaseUses()
    {
        var ct = TestContext.Current.CancellationToken;
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 10, PipelineId = 1, Status = PipelineStatus.Success, StartedAt = July },
            new PipelineRun { Id = 11, PipelineId = 1, Status = PipelineStatus.Success, StartedAt = July });
        var ownArtifact = new PipelineArtifact { Id = 10, PipelineId = 1, PipelineRunId = 10, ProjectId = 1, FilePath = "own.zip" };
        var sharedArtifact = new PipelineArtifact { Id = 11, PipelineId = 1, PipelineRunId = 11, ProjectId = 1, FilePath = "shared.zip" };
        var archivedOne = Add(new Release { Status = ReleaseStatus.Published, PublishedAt = July, CreatedByPipelineRunId = 10, PipelineRunId = 10, Artifacts = [ownArtifact, sharedArtifact] });
        var archivedTwo = Add(new Release { Status = ReleaseStatus.Published, PublishedAt = July, CreatedByPipelineRunId = 11 });
        var live = Add(new Release { Status = ReleaseStatus.Published, DetectedAt = Now, PublishedAt = Now.AddDays(-2), PipelineRunId = 11, Artifacts = [sharedArtifact] });
        AddNewest();
        var candidates = await ExpiredAsync();
        Assert.Equal([archivedOne.Id, archivedTwo.Id], candidates);

        Assert.Equal(2, await _repository.ArchiveExpiredAsync(candidates, Cutoff, Now, ct));

        Assert.False(await _db.PipelineArtifacts.AnyAsync(artifact => artifact.Id == 10, ct));
        Assert.False(await _db.PipelineRuns.AnyAsync(run => run.Id == 10, ct));
        // The live release still links the shared artifact and names run 11: both stay.
        Assert.True(await _db.PipelineArtifacts.AnyAsync(artifact => artifact.Id == 11, ct));
        Assert.True(await _db.PipelineRuns.AnyAsync(run => run.Id == 11, ct));
        var first = await _db.Releases.AsNoTracking().SingleAsync(release => release.Id == archivedOne.Id, ct);
        Assert.Null(first.CreatedByPipelineRunId);
        Assert.Null(first.PipelineRunId);
        Assert.Equal(11, (await _db.Releases.AsNoTracking().SingleAsync(release => release.Id == archivedTwo.Id, ct)).CreatedByPipelineRunId);
        Assert.Equal(ReleaseStatus.Published, (await _db.Releases.AsNoTracking().SingleAsync(release => release.Id == live.Id, ct)).Status);
    }

    [Fact]
    public async Task Archiving_LeavesProductionAndTheTwoPreviousDeploymentsIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var runs = Enumerable.Range(20, 4)
            .Select(id => new PipelineRun { Id = id, PipelineId = 1, Status = PipelineStatus.Success, StartedAt = July })
            .ToList();
        _db.PipelineRuns.AddRange(runs);
        var releases = Enumerable.Range(0, 4)
            .Select(index => Add(new Release
            {
                Status = index == 3 ? ReleaseStatus.Deployed : ReleaseStatus.Superseded,
                DetectedAt = July.AddDays(index),
                PublishedAt = July.AddDays(index),
                CreatedByPipelineRunId = 20 + index,
                Artifacts = [new PipelineArtifact { Id = 20 + index, PipelineId = 1, PipelineRunId = 20 + index, ProjectId = 1 }]
            }))
            .ToList();
        AddNewest();
        var candidates = await ExpiredAsync();
        Assert.Equal([releases[0].Id], candidates);

        Assert.Equal(1, await _repository.ArchiveExpiredAsync(candidates, Cutoff, Now, ct));

        Assert.Equal(ReleaseStatus.Archived, (await _db.Releases.AsNoTracking().SingleAsync(r => r.Id == releases[0].Id, ct)).Status);
        Assert.False(await _db.PipelineArtifacts.AnyAsync(artifact => artifact.Id == 20, ct));
        foreach (var index in new[] { 1, 2, 3 })
        {
            var kept = await _db.Releases.AsNoTracking().Include(r => r.Artifacts).SingleAsync(r => r.Id == releases[index].Id, ct);
            Assert.NotEqual(ReleaseStatus.Archived, kept.Status);
            Assert.Equal(20 + index, kept.CreatedByPipelineRunId);
            Assert.Single(kept.Artifacts);
            Assert.True(await _db.PipelineRuns.AnyAsync(run => run.Id == 20 + index, ct));
        }
    }

    [Fact]
    public async Task Archiving_UnlinksACompletedDelivery_AndDeletesItsArtifact()
    {
        var ct = TestContext.Current.CancellationToken;
        _db.PipelineRuns.Add(new PipelineRun { Id = 30, PipelineId = 1, Status = PipelineStatus.Success, StartedAt = July });
        var delivered = new PipelineArtifact { Id = 30, PipelineId = 1, PipelineRunId = 30, ProjectId = 1 };
        _db.DependencyTrackOutboxItems.Add(new DependencyTrackOutboxItem { Id = 30, PipelineArtifactId = 30, ProjectId = 1, CompletedAt = July });
        var release = Add(new Release { Status = ReleaseStatus.Published, PublishedAt = July, CreatedByPipelineRunId = 30, Artifacts = [delivered] });
        AddNewest();
        var candidates = await ExpiredAsync();

        Assert.Equal(1, await _repository.ArchiveExpiredAsync(candidates, Cutoff, Now, ct));

        Assert.False(await _db.PipelineArtifacts.AnyAsync(artifact => artifact.Id == 30, ct));
        Assert.Null((await _db.DependencyTrackOutboxItems.AsNoTracking().SingleAsync(item => item.Id == 30, ct)).PipelineArtifactId);
        Assert.False(await _db.PipelineRuns.AnyAsync(run => run.Id == 30, ct));
        Assert.Equal(ReleaseStatus.Archived, (await _db.Releases.AsNoTracking().SingleAsync(r => r.Id == release.Id, ct)).Status);
    }
}
