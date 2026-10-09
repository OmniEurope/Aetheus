// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Artifacts;

public class ArtifactRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly ArtifactRepository _repo;

    public ArtifactRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new ArtifactRepository(_db);

        // FindAsync / GetByProjectPagedAsync Include the REQUIRED Pipeline + PipelineRun navigations;
        // EF filters out rows whose required principal is missing, so seed them up front.
        _db.Pipelines.AddRange(
            new Pipeline { Id = 1, Name = "CI" },
            new Pipeline { Id = 2, Name = "CD" });
        _db.PipelineRuns.Add(new PipelineRun { Id = 1, PipelineId = 1, Status = PipelineStatus.Success });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetRunPipelineContextAsync_ResolvesTheProjectOfEnvironmentAndProjectServerPipelines()
    {
        // BACK-009: these pipelines have no direct ProjectId by design; their artifacts still have
        // a project, which carries their quota and retention.
        _db.Environments.Add(new Aetheus.Back.Data.Entities.Environment { Id = 7, Name = "prod", ProjectId = 40 });
        _db.ProjectServers.Add(new ProjectServer { Id = 8, ProjectId = 41 });
        _db.Pipelines.AddRange(
            new Pipeline { Id = 3, Name = "env-owned", EnvironmentId = 7 },
            new Pipeline { Id = 4, Name = "project-server-owned", ProjectServerId = 8 },
            new Pipeline { Id = 5, Name = "legacy-unowned" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 3, PipelineId = 3, Status = PipelineStatus.Running },
            new PipelineRun { Id = 4, PipelineId = 4, Status = PipelineStatus.Running },
            new PipelineRun { Id = 5, PipelineId = 5, Status = PipelineStatus.Running });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal((3, (int?)40), await _repo.GetRunPipelineContextAsync(3, TestContext.Current.CancellationToken));
        Assert.Equal((4, (int?)41), await _repo.GetRunPipelineContextAsync(4, TestContext.Current.CancellationToken));
        Assert.Equal((5, (int?)null), await _repo.GetRunPipelineContextAsync(5, TestContext.Current.CancellationToken));
    }

    private PipelineArtifact MakeArtifact(
        int pipelineId = 1, int projectId = 1, int runId = 1,
        ArtifactRetentionPolicy policy = ArtifactRetentionPolicy.Build,
        string? env = null, DateTime? createdAt = null, DateTime? expiresAt = null,
        string name = "build.zip")
        => new()
        {
            PipelineId = pipelineId,
            ProjectId = projectId,
            PipelineRunId = runId,
            Name = name,
            FilePath = $"/artifacts/{name}",
            RetentionPolicy = policy,
            EnvironmentName = env,
            CreatedAt = createdAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            RetentionExpiresAt = expiresAt ?? new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc)
        };

    [Fact]
    public async Task FindAsync_Found_ReturnsArtifact()
    {
        var a = MakeArtifact();
        await _repo.AddAsync(a, ct: TestContext.Current.CancellationToken);

        var found = await _repo.FindAsync(a.Id, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal("build.zip", found.Name);
    }

    [Fact]
    public async Task FindAsync_NotFound_ReturnsNull()
        => Assert.Null(await _repo.FindAsync(999, ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task GetByProjectPagedAsync_FiltersByProjectAndPaginates()
    {
        await _repo.AddAsync(MakeArtifact(projectId: 1, createdAt: new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), name: "a3"), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(projectId: 1, createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), name: "a1"), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(projectId: 2, name: "other"), ct: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetByProjectPagedAsync(1, null, null, 1, 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, total);
        Assert.Equal(2, items.Count);
        // Project filter keeps only project 1's artifacts (the InMemory split-query provider does not
        // guarantee positional order, so assert set membership rather than index).
        Assert.Contains(items, a => a.Name == "a3");
        Assert.Contains(items, a => a.Name == "a1");
        Assert.DoesNotContain(items, a => a.Name == "other");
    }

    [Fact]
    public async Task GetByProjectPagedAsync_FiltersByPolicyAndPipeline()
    {
        await _repo.AddAsync(MakeArtifact(projectId: 1, pipelineId: 1, policy: ArtifactRetentionPolicy.Released), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(projectId: 1, pipelineId: 1, policy: ArtifactRetentionPolicy.Build), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(projectId: 1, pipelineId: 2, policy: ArtifactRetentionPolicy.Released), ct: TestContext.Current.CancellationToken);

        var (released, releasedTotal) = await _repo.GetByProjectPagedAsync(1, ArtifactRetentionPolicy.Released, null, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, releasedTotal);

        var (pipe1, pipe1Total) = await _repo.GetByProjectPagedAsync(1, ArtifactRetentionPolicy.Released, 1, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, pipe1Total);
        Assert.Single(pipe1);
    }

    [Fact]
    public async Task GetByProjectPagedAsync_SortsByNameBeforePaging()
    {
        await _repo.AddAsync(MakeArtifact(name: "alpha"), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(name: "gamma"), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(name: "beta"), ct: TestContext.Current.CancellationToken);

        var first = await _repo.GetByProjectPagedAsync(1, null, null, 1, 2,
            ct: TestContext.Current.CancellationToken, sortBy: "Name", sortDescending: true);
        var second = await _repo.GetByProjectPagedAsync(1, null, null, 2, 2,
            ct: TestContext.Current.CancellationToken, sortBy: "Name", sortDescending: true);

        Assert.Equal(["gamma", "beta"], first.Items.Select(artifact => artifact.Name));
        Assert.Equal(["alpha"], second.Items.Select(artifact => artifact.Name));
    }

    [Fact]
    public async Task GetByPipelineAndProjectAsync_MatchesAllThree()
    {
        await _repo.AddAsync(MakeArtifact(pipelineId: 1, projectId: 1, policy: ArtifactRetentionPolicy.Deployed), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(pipelineId: 1, projectId: 1, policy: ArtifactRetentionPolicy.Build), ct: TestContext.Current.CancellationToken);

        var result = await _repo.GetByPipelineAndProjectAsync(1, 1, ArtifactRetentionPolicy.Deployed, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(ArtifactRetentionPolicy.Deployed, result[0].RetentionPolicy);
    }

    [Fact]
    public async Task GetByEnvironmentAsync_ReturnsDeployedForEnvironment()
    {
        await _repo.AddAsync(MakeArtifact(pipelineId: 1, projectId: 1, policy: ArtifactRetentionPolicy.Deployed, env: "prod"), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(pipelineId: 1, projectId: 1, policy: ArtifactRetentionPolicy.Deployed, env: "staging"), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(pipelineId: 1, projectId: 1, policy: ArtifactRetentionPolicy.Build, env: "prod"), ct: TestContext.Current.CancellationToken);

        var result = await _repo.GetByEnvironmentAsync(1, 1, "prod", ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("prod", result[0].EnvironmentName);
    }

    [Fact]
    public async Task GetReleasesAsync_ReturnsOnlyReleasedPolicy()
    {
        await _repo.AddAsync(MakeArtifact(pipelineId: 1, projectId: 1, policy: ArtifactRetentionPolicy.Released), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(pipelineId: 1, projectId: 1, policy: ArtifactRetentionPolicy.Build), ct: TestContext.Current.CancellationToken);

        var result = await _repo.GetReleasesAsync(1, 1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(ArtifactRetentionPolicy.Released, result[0].RetentionPolicy);
    }

    [Fact]
    public async Task GetExpiredAsync_ReturnsArtifactsAtOrBeforeCutoffOrderedByExpiry()
    {
        await _repo.AddAsync(MakeArtifact(expiresAt: new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), name: "exp-early"), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(expiresAt: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc), name: "exp-late"), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(expiresAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), name: "not-expired"), ct: TestContext.Current.CancellationToken);

        var cutoff = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);
        var result = await _repo.GetExpiredAsync(cutoff, 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("exp-early", result[0].Name);
        Assert.Equal("exp-late", result[1].Name);
    }

    [Fact]
    public async Task GetExpiredAsync_PreservesArtifactOfCurrentlyDeployedRelease()
    {
        var artifact = MakeArtifact(
            expiresAt: new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            name: "deployed-baseline");
        await _repo.AddAsync(artifact, ct: TestContext.Current.CancellationToken);
        var release = new Release
        {
            ProjectId = artifact.ProjectId!.Value,
            Version = "1.0.0",
            Status = ReleaseStatus.Deployed,
            Artifacts = [artifact]
        };
        _db.Releases.Add(release);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetExpiredAsync(
            new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
            10,
            ct: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result, item => item.Id == artifact.Id);
    }

    [Fact]
    public async Task GetExpiredAsync_KeepsTheLastThreeDeployedReleasesAndFreesTheFourth()
    {
        // PLAN-007 lot 5: N-1 must stay redeployable. After a fourth deployment, the deployed release
        // and the two it superseded keep their payloads; only the oldest superseded one ages out.
        var expired = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var artifacts = new List<PipelineArtifact>();
        for (var index = 0; index < 4; index++)
        {
            var artifact = MakeArtifact(expiresAt: expired, name: $"payload-{index}");
            await _repo.AddAsync(artifact, ct: TestContext.Current.CancellationToken);
            artifacts.Add(artifact);
            _db.Releases.Add(new Release
            {
                ProjectId = 1,
                Version = $"1.0.{index}",
                Status = index == 3 ? ReleaseStatus.Deployed : ReleaseStatus.Superseded,
                PublishedAt = new DateTime(2026, 1, 1 + index, 0, 0, 0, DateTimeKind.Utc),
                Artifacts = [artifact]
            });
        }
        // Another project's deployments must not push this project's releases out of the window.
        _db.Releases.Add(new Release
        {
            ProjectId = 2,
            Version = "9.9.9",
            Status = ReleaseStatus.Deployed,
            PublishedAt = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc)
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetExpiredAsync(
            new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
            10,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(artifacts[0].Id, Assert.Single(result).Id);
    }

    [Fact]
    public async Task GetExpiredAsync_PreservesArtifactWithActiveCheckpointLease()
    {
        var artifact = MakeArtifact(
            expiresAt: new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            name: "checkpoint");
        artifact.RetentionLeaseExpiresAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        await _repo.AddAsync(artifact, ct: TestContext.Current.CancellationToken);

        var result = await _repo.GetExpiredAsync(
            new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
            10,
            ct: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result, item => item.Id == artifact.Id);
    }

    [Fact]
    public async Task GetExpiredAsync_RespectsBatchSize()
    {
        for (var i = 0; i < 5; i++)
            await _repo.AddAsync(MakeArtifact(expiresAt: new DateTime(2026, 1, i + 1, 0, 0, 0, DateTimeKind.Utc), name: $"e{i}"), ct: TestContext.Current.CancellationToken);

        var result = await _repo.GetExpiredAsync(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), 3, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetProjectBuildArtifactsAsync_ReturnsOnlyProjectBuildsOldestFirst()
    {
        var linkedBuild = MakeArtifact(projectId: 1, policy: ArtifactRetentionPolicy.Build,
            createdAt: new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc), name: "linked-release");
        await _repo.AddAsync(MakeArtifact(projectId: 1, policy: ArtifactRetentionPolicy.Build,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), name: "old"), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(projectId: 1, policy: ArtifactRetentionPolicy.Build,
            createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), name: "new"), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(projectId: 1, policy: ArtifactRetentionPolicy.Released, name: "released"), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(projectId: 2, policy: ArtifactRetentionPolicy.Build, name: "other-project"), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(linkedBuild, ct: TestContext.Current.CancellationToken);
        var release = new Release { Id = 51, ProjectId = 1, Version = "1.0.0" };
        release.Artifacts.Add(linkedBuild);
        _db.Releases.Add(release);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetProjectBuildArtifactsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["old", "new"], result.Select(a => a.Name));
    }

    [Fact]
    public async Task LinkReleaseAsync_ExistingRelease_LinksOnce()
    {
        var a = MakeArtifact();
        await _repo.AddAsync(a, ct: TestContext.Current.CancellationToken);
        _db.Releases.Add(new Release { Id = 50, ProjectId = 1, Version = "1.0.0" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.LinkReleaseAsync(a, 50, ct: TestContext.Current.CancellationToken);
        await _repo.LinkReleaseAsync(a, 50, ct: TestContext.Current.CancellationToken); // idempotent
        await _repo.SaveChangesAsync(ct: TestContext.Current.CancellationToken);

        Assert.Single(a.Releases);
        Assert.Equal(50, a.Releases.First().Id);
    }

    [Fact]
    public async Task LinkReleaseAsync_MissingRelease_NoOp()
    {
        var a = MakeArtifact();
        await _repo.AddAsync(a, ct: TestContext.Current.CancellationToken);

        await _repo.LinkReleaseAsync(a, 999, ct: TestContext.Current.CancellationToken);

        Assert.Empty(a.Releases);
    }

    [Fact]
    public async Task RemoveAsync_DeletesArtifact()
    {
        var a = MakeArtifact();
        await _repo.AddAsync(a, ct: TestContext.Current.CancellationToken);

        Assert.True(await _repo.RemoveAsync(a, ct: TestContext.Current.CancellationToken));

        Assert.Equal(0, await _db.PipelineArtifacts.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task R463_RemoveAsync_AnArtifactAnotherPathAlreadyRemoved_IsNotAFailure()
    {
        var a = MakeArtifact();
        await _repo.AddAsync(a, ct: TestContext.Current.CancellationToken);
        var stale = new PipelineArtifact { Id = a.Id, FilePath = a.FilePath };
        Assert.True(await _repo.RemoveAsync(a, ct: TestContext.Current.CancellationToken));

        // The copy selected before the other path deleted the row: no concurrency exception, just false.
        Assert.False(await _repo.RemoveAsync(stale, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetNextBuildNumberAsync_NoArtifacts_ReturnsOne()
        => Assert.Equal(1, await _repo.GetNextBuildNumberAsync(1, 1, ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task GetNextBuildNumberAsync_ReturnsMaxRunIdPlusOne()
    {
        await _repo.AddAsync(MakeArtifact(projectId: 1, pipelineId: 1, runId: 7), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(projectId: 1, pipelineId: 1, runId: 3), ct: TestContext.Current.CancellationToken);

        Assert.Equal(8, await _repo.GetNextBuildNumberAsync(1, 1, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindSuccessfulPipelineArtifactByCommitAsync_RequiresExactSuccessfulProvenance()
    {
        const string expectedCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        _db.Projects.Add(new Project { Id = 7, Name = "Aetheus" });
        _db.Pipelines.Add(new Pipeline { Id = 3, Name = "aetheus-ci", ProjectId = 7 });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 3, PipelineId = 3, CommitHash = expectedCommit, Status = PipelineStatus.Success },
            new PipelineRun { Id = 4, PipelineId = 3, CommitHash = expectedCommit, Status = PipelineStatus.Failed },
            new PipelineRun { Id = 5, PipelineId = 3, CommitHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Status = PipelineStatus.Success });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(pipelineId: 3, projectId: 7, runId: 3, name: "package"), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(pipelineId: 3, projectId: 7, runId: 4, name: "failed"), ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(MakeArtifact(pipelineId: 3, projectId: 7, runId: 5, name: "other-commit"), ct: TestContext.Current.CancellationToken);

        var artifact = await _repo.FindSuccessfulPipelineArtifactByCommitAsync(
            7, "aetheus-ci", expectedCommit, "package", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(artifact);
        Assert.Equal(3, artifact.PipelineRunId);
        Assert.Null(await _repo.FindSuccessfulPipelineArtifactByCommitAsync(
            7, "aetheus-ci", expectedCommit, "failed", ct: TestContext.Current.CancellationToken));
        Assert.Null(await _repo.FindSuccessfulPipelineArtifactByCommitAsync(
            7, "aetheus-ci", expectedCommit, "other-commit", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindLatestSuccessfulPipelineArtifactAsync_IgnoresTheCommitButNothingElse()
    {
        // The selector drops the commit constraint and only that one: the run must still have
        // succeeded, and the artifact must still come from that pipeline, that name, that project.
        _db.Projects.AddRange(
            new Project { Id = 9, Name = "Nightly project" },
            new Project { Id = 10, Name = "Other project" });
        _db.Pipelines.AddRange(
            new Pipeline { Id = 6, Name = "aetheus-nightly", ProjectId = 9 },
            new Pipeline { Id = 7, Name = "aetheus-security", ProjectId = 9 },
            new Pipeline { Id = 8, Name = "aetheus-nightly", ProjectId = 10 });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 6, PipelineId = 6, Status = PipelineStatus.Success },
            new PipelineRun { Id = 7, PipelineId = 6, Status = PipelineStatus.Success },
            new PipelineRun { Id = 8, PipelineId = 6, Status = PipelineStatus.Failed },
            new PipelineRun { Id = 9, PipelineId = 7, Status = PipelineStatus.Success },
            new PipelineRun { Id = 11, PipelineId = 8, Status = PipelineStatus.Success });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var january = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _repo.AddAsync(
            MakeArtifact(pipelineId: 6, projectId: 9, runId: 6, createdAt: january, name: "evidence"),
            ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(
            MakeArtifact(pipelineId: 6, projectId: 9, runId: 7, createdAt: january.AddDays(30), name: "evidence"),
            ct: TestContext.Current.CancellationToken);
        // Newest of them all, but its run failed.
        await _repo.AddAsync(
            MakeArtifact(pipelineId: 6, projectId: 9, runId: 8, createdAt: january.AddDays(60), name: "evidence"),
            ct: TestContext.Current.CancellationToken);
        // Newer still, same project and name, but another pipeline produced it.
        await _repo.AddAsync(
            MakeArtifact(pipelineId: 7, projectId: 9, runId: 9, createdAt: january.AddDays(90), name: "evidence"),
            ct: TestContext.Current.CancellationToken);
        await _repo.AddAsync(
            MakeArtifact(pipelineId: 8, projectId: 10, runId: 11, createdAt: january.AddDays(120), name: "evidence"),
            ct: TestContext.Current.CancellationToken);

        var artifact = await _repo.FindLatestSuccessfulPipelineArtifactAsync(
            9, "aetheus-nightly", "evidence", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(artifact);
        Assert.Equal(7, artifact.PipelineRunId);
        Assert.Null(await _repo.FindLatestSuccessfulPipelineArtifactAsync(
            9, "aetheus-nightly", "another-artifact", ct: TestContext.Current.CancellationToken));
        var otherProject = await _repo.FindLatestSuccessfulPipelineArtifactAsync(
            10, "aetheus-nightly", "evidence", ct: TestContext.Current.CancellationToken);
        Assert.Equal(11, otherProject?.PipelineRunId);
    }

    [Fact]
    public async Task FindReleaseArtifactAsync_LatestPublished_IgnoresNewerDraftAndFailedReleases()
    {
        _db.Projects.Add(new Project { Id = 8, Name = "Release project" });
        var artifact = MakeArtifact(projectId: 8, name: "published-package");
        await _repo.AddAsync(artifact, ct: TestContext.Current.CancellationToken);
        var published = new Release
        {
            Id = 80,
            ProjectId = 8,
            Version = "1.2.0",
            Status = ReleaseStatus.Published,
            DetectedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PublishedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        };
        published.Artifacts.Add(artifact);
        _db.Releases.AddRange(
            published,
            new Release { Id = 81, ProjectId = 8, Version = "1.3.0-draft", Status = ReleaseStatus.Detected, DetectedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc) },
            new Release { Id = 82, ProjectId = 8, Version = "1.3.0-failed", Status = ReleaseStatus.Failed, DetectedAt = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var resolved = await _repo.FindReleaseArtifactAsync(8, "latest-published", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(resolved);
        Assert.Equal(artifact.Id, resolved.Id);
    }

    [Fact]
    public async Task FindReleaseArtifactAsync_LatestPublished_SkipsNewerPublishedMetadataWithoutArtifact()
    {
        _db.Projects.Add(new Project { Id = 9, Name = "Rollback project" });
        var artifact = MakeArtifact(projectId: 9, name: "retained-package");
        await _repo.AddAsync(artifact, ct: TestContext.Current.CancellationToken);
        var retained = new Release
        {
            Id = 90,
            ProjectId = 9,
            Version = "1.0.0",
            Status = ReleaseStatus.Published,
            DetectedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PublishedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        };
        retained.Artifacts.Add(artifact);
        _db.Releases.AddRange(
            retained,
            new Release
            {
                Id = 91,
                ProjectId = 9,
                Version = "1.1.0",
                Status = ReleaseStatus.Published,
                DetectedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
                PublishedAt = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc)
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var resolved = await _repo.FindReleaseArtifactAsync(9, "latest-published", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(resolved);
        Assert.Equal(artifact.Id, resolved.Id);
    }

    [Fact]
    public async Task FindReleaseArtifactAsync_CurrentDeployed_ReturnsExactActiveReleaseAtSameCommit()
    {
        const string sameCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        _db.Projects.Add(new Project { Id = 10, Name = "Current deployment project" });
        _db.Pipelines.Add(new Pipeline { Id = 100, ProjectId = 10, Name = "toto-candidate" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 100, PipelineId = 100, Status = PipelineStatus.Success, CommitHash = sameCommit },
            new PipelineRun { Id = 101, PipelineId = 100, Status = PipelineStatus.Success, CommitHash = sameCommit });
        var supersededArtifact = MakeArtifact(
            pipelineId: 100, projectId: 10, runId: 100, name: "superseded-package");
        var deployedArtifact = MakeArtifact(
            pipelineId: 100, projectId: 10, runId: 101, name: "deployed-package");
        await _repo.AddAsync(supersededArtifact, TestContext.Current.CancellationToken);
        await _repo.AddAsync(deployedArtifact, TestContext.Current.CancellationToken);
        var superseded = new Release
        {
            Id = 100,
            ProjectId = 10,
            Version = "candidate-a",
            Status = ReleaseStatus.Superseded,
            DetectedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        superseded.Artifacts.Add(supersededArtifact);
        var deployed = new Release
        {
            Id = 101,
            ProjectId = 10,
            Version = "candidate-b",
            Status = ReleaseStatus.Deployed,
            DetectedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        };
        deployed.Artifacts.Add(deployedArtifact);
        _db.Releases.AddRange(superseded, deployed);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolved = await _repo.FindReleaseArtifactAsync(
            10, "current-deployed", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(resolved);
        Assert.Equal(deployedArtifact.Id, resolved.Id);
    }

    [Fact]
    public async Task FindPreviousPublishedReleaseArtifactAsync_SkipsNewestArtifactFromCurrentCommit()
    {
        const string previousCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string currentCommit = "cccccccccccccccccccccccccccccccccccccccc";
        _db.Projects.Add(new Project { Id = 12, Name = "Previous release project" });
        _db.Pipelines.Add(new Pipeline { Id = 120, ProjectId = 12, Name = "aetheus-ci" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 120, PipelineId = 120, Status = PipelineStatus.Success, CommitHash = previousCommit },
            new PipelineRun { Id = 121, PipelineId = 120, Status = PipelineStatus.Success, CommitHash = currentCommit });
        var previousArtifact = MakeArtifact(pipelineId: 120, projectId: 12, runId: 120, name: "previous-package");
        var currentArtifact = MakeArtifact(pipelineId: 120, projectId: 12, runId: 121, name: "current-package");
        await _repo.AddAsync(previousArtifact, TestContext.Current.CancellationToken);
        await _repo.AddAsync(currentArtifact, TestContext.Current.CancellationToken);
        var previousRelease = new Release
        {
            Id = 120,
            ProjectId = 12,
            Version = "1.0.0",
            Status = ReleaseStatus.Published,
            DetectedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PublishedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        };
        previousRelease.Artifacts.Add(previousArtifact);
        var currentRelease = new Release
        {
            Id = 121,
            ProjectId = 12,
            Version = "1.1.0",
            Status = ReleaseStatus.Published,
            DetectedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            PublishedAt = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc)
        };
        currentRelease.Artifacts.Add(currentArtifact);
        _db.Releases.AddRange(previousRelease, currentRelease);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolved = await _repo.FindPreviousPublishedReleaseArtifactAsync(
            12, currentCommit, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(resolved);
        Assert.Equal(previousArtifact.Id, resolved.Id);
    }

    [Fact]
    public async Task FindPreviousDeployedReleaseArtifactAsync_IgnoresNewerPublishedRelease()
    {
        const string deployedCommit = "dddddddddddddddddddddddddddddddddddddddd";
        const string currentCommit = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        _db.Projects.Add(new Project { Id = 13, Name = "Active deployment project" });
        _db.Pipelines.Add(new Pipeline { Id = 130, ProjectId = 13, Name = "aetheus-ci" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 130, PipelineId = 130, Status = PipelineStatus.Success, CommitHash = deployedCommit },
            new PipelineRun { Id = 131, PipelineId = 130, Status = PipelineStatus.Success, CommitHash = currentCommit });
        var deployedArtifact = MakeArtifact(pipelineId: 130, projectId: 13, runId: 130, name: "deployed-package");
        var publishedArtifact = MakeArtifact(pipelineId: 130, projectId: 13, runId: 131, name: "published-package");
        await _repo.AddAsync(deployedArtifact, TestContext.Current.CancellationToken);
        await _repo.AddAsync(publishedArtifact, TestContext.Current.CancellationToken);
        var deployedRelease = new Release
        {
            Id = 130,
            ProjectId = 13,
            Version = "1.0.0",
            Status = ReleaseStatus.Deployed,
            PublishedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        };
        deployedRelease.Artifacts.Add(deployedArtifact);
        var publishedRelease = new Release
        {
            Id = 131,
            ProjectId = 13,
            Version = "1.1.0",
            Status = ReleaseStatus.Published,
            PublishedAt = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc)
        };
        publishedRelease.Artifacts.Add(publishedArtifact);
        _db.Releases.AddRange(deployedRelease, publishedRelease);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolved = await _repo.FindPreviousDeployedReleaseArtifactAsync(
            13, currentCommit, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(resolved);
        Assert.Equal(deployedArtifact.Id, resolved.Id);
    }

    [Fact]
    public async Task FindPreviousDeployedReleaseArtifactAsync_SelectsSupersededDeploymentBeforeCurrentCommit()
    {
        const string previousCommit = "1111111111111111111111111111111111111111";
        const string currentCommit = "2222222222222222222222222222222222222222";
        _db.Projects.Add(new Project { Id = 14, Name = "Deployment lineage project" });
        _db.Pipelines.Add(new Pipeline { Id = 140, ProjectId = 14, Name = "aetheus-ci" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 140, PipelineId = 140, Status = PipelineStatus.Success, CommitHash = previousCommit },
            new PipelineRun { Id = 141, PipelineId = 140, Status = PipelineStatus.Success, CommitHash = currentCommit });
        var previousArtifact = MakeArtifact(pipelineId: 140, projectId: 14, runId: 140, name: "previous-deployed-package");
        var currentArtifact = MakeArtifact(pipelineId: 140, projectId: 14, runId: 141, name: "current-deployed-package");
        await _repo.AddAsync(previousArtifact, TestContext.Current.CancellationToken);
        await _repo.AddAsync(currentArtifact, TestContext.Current.CancellationToken);
        var previousRelease = new Release
        {
            Id = 140,
            ProjectId = 14,
            Version = "1.0.0",
            Status = ReleaseStatus.Superseded,
            PublishedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        };
        previousRelease.Artifacts.Add(previousArtifact);
        var currentRelease = new Release
        {
            Id = 141,
            ProjectId = 14,
            Version = "1.1.0",
            Status = ReleaseStatus.Deployed,
            PublishedAt = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc)
        };
        currentRelease.Artifacts.Add(currentArtifact);
        _db.Releases.AddRange(previousRelease, currentRelease);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolved = await _repo.FindPreviousDeployedReleaseArtifactAsync(
            14, currentCommit, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(resolved);
        Assert.Equal(previousArtifact.Id, resolved.Id);
    }

    [Fact]
    public async Task FindPreviousDeployedReleaseArtifactAsync_WithName_IgnoresNewerLinkedReport()
    {
        const string previousCommit = "3333333333333333333333333333333333333333";
        const string currentCommit = "4444444444444444444444444444444444444444";
        _db.Projects.Add(new Project { Id = 15, Name = "Named rollback artifact project" });
        _db.Pipelines.Add(new Pipeline { Id = 150, ProjectId = 15, Name = "aetheus-ci" });
        _db.PipelineRuns.Add(new PipelineRun
        {
            Id = 150,
            PipelineId = 150,
            Status = PipelineStatus.Success,
            CommitHash = previousCommit
        });
        var package = MakeArtifact(
            pipelineId: 150, projectId: 15, runId: 150,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            name: "BuildArtifacts-artifacts");
        var report = MakeArtifact(
            pipelineId: 150, projectId: 15, runId: 150,
            createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            name: "analysis-dotnet-architecture-findings");
        await _repo.AddAsync(package, TestContext.Current.CancellationToken);
        await _repo.AddAsync(report, TestContext.Current.CancellationToken);
        var release = new Release
        {
            Id = 150,
            ProjectId = 15,
            Version = "1.0.0",
            Status = ReleaseStatus.Deployed,
            PublishedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)
        };
        release.Artifacts.AddRange([package, report]);
        _db.Releases.Add(release);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolved = await _repo.FindPreviousDeployedReleaseArtifactAsync(
            15, currentCommit, "BuildArtifacts-artifacts", TestContext.Current.CancellationToken);

        Assert.NotNull(resolved);
        Assert.Equal(package.Id, resolved.Id);
    }

    [Fact]
    public async Task FindPreviousDeployedReleaseArtifactAsync_DoesNotFallBackToOlderReleaseForNamedArtifact()
    {
        const string olderCommit = "5555555555555555555555555555555555555555";
        const string previousCommit = "6666666666666666666666666666666666666666";
        const string currentCommit = "7777777777777777777777777777777777777777";
        _db.Projects.Add(new Project { Id = 16, Name = "Exact predecessor project" });
        _db.Pipelines.Add(new Pipeline { Id = 160, ProjectId = 16, Name = "aetheus-ci" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 160, PipelineId = 160, Status = PipelineStatus.Success, CommitHash = olderCommit },
            new PipelineRun { Id = 161, PipelineId = 160, Status = PipelineStatus.Success, CommitHash = previousCommit });
        var olderRuntime = MakeArtifact(pipelineId: 160, projectId: 16, runId: 160, name: "QaRuntime-artifacts");
        var previousPayload = MakeArtifact(pipelineId: 160, projectId: 16, runId: 161, name: "ApplicationPayload-artifacts");
        await _repo.AddAsync(olderRuntime, TestContext.Current.CancellationToken);
        await _repo.AddAsync(previousPayload, TestContext.Current.CancellationToken);
        var olderRelease = new Release
        {
            Id = 160,
            ProjectId = 16,
            Version = "c-older",
            Status = ReleaseStatus.Superseded,
            PublishedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        };
        olderRelease.Artifacts.Add(olderRuntime);
        var previousRelease = new Release
        {
            Id = 161,
            ProjectId = 16,
            Version = "c-previous",
            Status = ReleaseStatus.Deployed,
            PublishedAt = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc)
        };
        previousRelease.Artifacts.Add(previousPayload);
        _db.Releases.AddRange(olderRelease, previousRelease);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var resolved = await _repo.FindPreviousDeployedReleaseArtifactAsync(
            16, currentCommit, "QaRuntime-artifacts", TestContext.Current.CancellationToken);

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("aetheus-release-fast", "QaRuntime-artifacts", false)]
    [InlineData("aetheus-release-fast", "ApplicationPayload-artifacts", true)]
    [InlineData("aetheus-candidate", "QaRuntime-artifacts", true)]
    public async Task RequiresPreviousDeployedArtifactAsync_AppliesSelectedReleaseContract(
        string pipelineName,
        string artifactName,
        bool expected)
    {
        const string previousCommit = "8888888888888888888888888888888888888888";
        const string currentCommit = "9999999999999999999999999999999999999999";
        _db.Projects.Add(new Project { Id = 17, Name = "Contract-specific artifact project" });
        _db.Pipelines.Add(new Pipeline { Id = 170, ProjectId = 17, Name = pipelineName });
        _db.PipelineRuns.Add(new PipelineRun
        {
            Id = 170,
            PipelineId = 170,
            Status = PipelineStatus.Success,
            CommitHash = previousCommit
        });
        var payload = MakeArtifact(
            pipelineId: 170, projectId: 17, runId: 170, name: "ApplicationPayload-artifacts");
        await _repo.AddAsync(payload, TestContext.Current.CancellationToken);
        var release = new Release
        {
            Id = 170,
            ProjectId = 17,
            PipelineRunId = 170,
            Version = "c-previous",
            Status = ReleaseStatus.Deployed,
            PublishedAt = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc)
        };
        release.Artifacts.Add(payload);
        _db.Releases.Add(release);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var required = await _repo.RequiresPreviousDeployedArtifactAsync(
            17, currentCommit, artifactName, TestContext.Current.CancellationToken);

        Assert.Equal(expected, required);
    }

    /// <summary>
    /// Recette R2-121: a project onboarded through the setup wizard names its pipelines after itself
    /// (portfolio-candidate, portfolio-release-fast); what makes them a candidate or a fast release is
    /// the shipped template they extend, not the aetheus-* name of the self-deploy pipelines.
    /// </summary>
    [Theory]
    [InlineData("portfolio-candidate", "application-candidate", "QaRuntime-artifacts", true)]
    [InlineData("portfolio-release-fast", "application-release-fast", "QaRuntime-artifacts", false)]
    [InlineData("portfolio-release-fast", "application-release-fast", "ApplicationPayload-artifacts", true)]
    [InlineData("portfolio-ci", "application-ci", "ApplicationPayload-artifacts", false)]
    public async Task RequiresPreviousDeployedArtifactAsync_RecognisesTheContractByTemplate(
        string pipelineName,
        string templateName,
        string artifactName,
        bool expected)
    {
        const string previousCommit = "8888888888888888888888888888888888888888";
        const string currentCommit = "9999999999999999999999999999999999999999";
        _db.Projects.Add(new Project { Id = 18, Name = "Portfolio" });
        _db.Pipelines.Add(new Pipeline
        {
            Id = 180,
            ProjectId = 18,
            Name = pipelineName,
            TemplateReferenceName = templateName,
            TemplateReferenceVersion = 1
        });
        _db.PipelineRuns.Add(new PipelineRun
        {
            Id = 180,
            PipelineId = 180,
            Status = PipelineStatus.Success,
            CommitHash = previousCommit
        });
        var payload = MakeArtifact(
            pipelineId: 180, projectId: 18, runId: 180, name: "ApplicationPayload-artifacts");
        await _repo.AddAsync(payload, TestContext.Current.CancellationToken);
        var release = new Release
        {
            Id = 180,
            ProjectId = 18,
            PipelineRunId = 180,
            Version = "c-previous",
            Status = ReleaseStatus.Deployed,
            PublishedAt = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc)
        };
        release.Artifacts.Add(payload);
        _db.Releases.Add(release);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, await _repo.RequiresPreviousDeployedArtifactAsync(
            18, currentCommit, artifactName, TestContext.Current.CancellationToken));
        Assert.Equal(templateName != "application-ci", await _repo.HasDeployedRollbackContractReleaseAsync(
            18, TestContext.Current.CancellationToken));
        Assert.Equal(templateName != "application-ci", await _repo.HasPublishedRollbackContractReleaseAsync(
            18, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HasPublishedRollbackContractReleaseAsync_DistinguishesOrdinaryAndContractReleases()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "Contract project" });
        _db.Pipelines.AddRange(
            new Pipeline { Id = 10, ProjectId = 10, Name = "aetheus-release" },
            new Pipeline { Id = 11, ProjectId = 10, Name = "aetheus-release-with-rollback" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 10, PipelineId = 10, Status = PipelineStatus.Success },
            new PipelineRun { Id = 11, PipelineId = 11, Status = PipelineStatus.Success });
        _db.Releases.Add(new Release
        {
            Id = 100,
            ProjectId = 10,
            PipelineRunId = 10,
            Version = "1.0.0",
            Status = ReleaseStatus.Published
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(await _repo.HasPublishedRollbackContractReleaseAsync(10, ct: TestContext.Current.CancellationToken));

        _db.Releases.Add(new Release
        {
            Id = 101,
            ProjectId = 10,
            PipelineRunId = 11,
            Version = "1.1.0",
            Status = ReleaseStatus.Published
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(await _repo.HasPublishedRollbackContractReleaseAsync(10, ct: TestContext.Current.CancellationToken));

        var artifact = MakeArtifact(projectId: 10, runId: 11, name: "rollback-contract");
        await _repo.AddAsync(artifact, ct: TestContext.Current.CancellationToken);
        var contractRelease = await _db.Releases.FindAsync([101], TestContext.Current.CancellationToken);
        Assert.NotNull(contractRelease);
        contractRelease.Artifacts.Add(artifact);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await _repo.HasPublishedRollbackContractReleaseAsync(10, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HasDeployedRollbackContractReleaseAsync_RecognizesCanonicalCandidateWithRetainedArtifact()
    {
        _db.Projects.Add(new Project { Id = 12, Name = "Candidate project" });
        _db.Pipelines.Add(new Pipeline { Id = 12, ProjectId = 12, Name = "aetheus-candidate" });
        _db.PipelineRuns.Add(new PipelineRun { Id = 12, PipelineId = 12, Status = PipelineStatus.Success });
        var release = new Release
        {
            Id = 120,
            ProjectId = 12,
            PipelineRunId = 12,
            Version = "c-01234567",
            Status = ReleaseStatus.Deployed
        };
        _db.Releases.Add(release);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(await _repo.HasDeployedRollbackContractReleaseAsync(
            12, ct: TestContext.Current.CancellationToken));

        var artifact = MakeArtifact(projectId: 12, runId: 12, name: "ApplicationPayload-artifacts");
        await _repo.AddAsync(artifact, ct: TestContext.Current.CancellationToken);
        release.Artifacts.Add(artifact);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await _repo.HasDeployedRollbackContractReleaseAsync(
            12, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HasDeployedRollbackContractReleaseAsync_RecognizesFastReleaseWithRetainedArtifact()
    {
        _db.Projects.Add(new Project { Id = 13, Name = "Fast release project" });
        _db.Pipelines.Add(new Pipeline { Id = 13, ProjectId = 13, Name = "aetheus-release-fast" });
        _db.PipelineRuns.Add(new PipelineRun { Id = 13, PipelineId = 13, Status = PipelineStatus.Success });
        var release = new Release
        {
            Id = 130,
            ProjectId = 13,
            PipelineRunId = 13,
            Version = "c-01234567",
            Status = ReleaseStatus.Deployed
        };
        _db.Releases.Add(release);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var artifact = MakeArtifact(projectId: 13, runId: 13, name: "ApplicationPayload-artifacts");
        await _repo.AddAsync(artifact, ct: TestContext.Current.CancellationToken);
        release.Artifacts.Add(artifact);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await _repo.HasDeployedRollbackContractReleaseAsync(
            13, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetProjectRetentionOverridesAsync_ReturnsOverrides()
    {
        _db.Projects.Add(new Project { Id = 1, Name = "P", ArtifactRetentionDays = 30, ArtifactLatestRetentionDays = 90 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (defaultDays, latestDays) = await _repo.GetProjectRetentionOverridesAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(30, defaultDays);
        Assert.Equal(90, latestDays);
    }

    [Fact]
    public async Task GetProjectRetentionOverridesAsync_MissingProject_ReturnsNulls()
    {
        var (defaultDays, latestDays) = await _repo.GetProjectRetentionOverridesAsync(404, ct: TestContext.Current.CancellationToken);

        Assert.Null(defaultDays);
        Assert.Null(latestDays);
    }
}
