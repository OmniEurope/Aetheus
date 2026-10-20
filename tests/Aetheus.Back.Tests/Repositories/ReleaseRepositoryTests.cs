// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Repositories;

public class ReleaseRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly ReleaseRepository _repo;
    private readonly int _projectId;

    public ReleaseRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new ReleaseRepository(_db);

        var project = new Project { Name = "P" };
        _db.Projects.Add(project);
        _db.SaveChanges();
        _projectId = project.Id;
    }

    [Fact]
    public async Task GetReleasesPagedAsync_AppliesColumnFilters_AndResolvedIds_BeforeTheCount()
    {
        // Publication range, status and grade lists, project name and the ids the source pipeline
        // filter resolved to all narrow the query.
        _db.Releases.AddRange(
            new Release { Id = 11, ProjectId = _projectId, Version = "1.0.0", BranchName = "main", Status = ReleaseStatus.Published, AssuranceGrade = AnalysisGrade.A, PublishedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc) },
            new Release { Id = 12, ProjectId = _projectId, Version = "1.1.0", BranchName = "main", Status = ReleaseStatus.Published, AssuranceGrade = AnalysisGrade.C, PublishedAt = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc) },
            new Release { Id = 13, ProjectId = _projectId, Version = "1.2.0", BranchName = "main", Status = ReleaseStatus.Detected, PublishedAt = null },
            new Release { Id = 14, ProjectId = _projectId, Version = "0.9.0", BranchName = "main", Status = ReleaseStatus.Published, AssuranceGrade = AnalysisGrade.A, PublishedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var separator = GridFilter.ListSeparator;

        var (items, total) = await _repo.GetReleasesPagedAsync(null, null, 1, 10, ct: TestContext.Current.CancellationToken,
            columnFilters:
            [
                new GridFilter
                {
                    Field = "PublishedAt", Operator = GridFilterOperator.GreaterThanOrEqual, Value = "2026-09-01",
                    SecondOperator = GridFilterOperator.LessThan, SecondValue = "2026-09-10"
                },
                new GridFilter { Field = "Status", Operator = GridFilterOperator.In, Value = "Published" },
                new GridFilter { Field = "AssuranceGrade", Operator = GridFilterOperator.In, Value = $"A{separator}B" },
                new GridFilter { Field = "ProjectName", Operator = GridFilterOperator.In, Value = "p" }
            ]);
        var (_, resolvedTotal) = await _repo.GetReleasesPagedAsync(null, null, 1, 10, ct: TestContext.Current.CancellationToken,
            releaseIds: [12, 13]);
        var facts = await _repo.GetReleaseFilterFactsAsync(null, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, total);
        Assert.Equal("1.0.0", Assert.Single(items).Version);
        Assert.Equal(2, resolvedTotal);
        Assert.Equal(4, facts.Count);
        Assert.All(facts, fact => Assert.Equal("P", fact.ProjectName));
    }

    [Fact]
    public void SourcePipelineFilter_MatchesTheTickedRootPipelines_WithoutCase()
    {
        var ids = ReleaseListQuery.WithSourcePipelineAmong(
            new GridFilter { Field = "SourcePipelineName", Operator = GridFilterOperator.In, Value = $"DEPLOY{GridFilter.ListSeparator}nightly" },
            [(1, "deploy"), (2, "build"), (3, null), (4, "Nightly")]);

        Assert.Equal([1, 4], ids.Order());
    }

    [Fact]
    public async Task GetReleasesPagedAsync_ReturnsPagedByPublishedAtDesc()
    {
        // Insertion order is deliberately NOT the expected sort order: the newest release is
        // inserted first (lowest Id), so the test fails if ordering falls back to Id instead
        // of PublishedAt.
        _db.Releases.AddRange(
            new Release { ProjectId = _projectId, Version = "3.0.0", BranchName = "main", PublishedAt = DateTime.UtcNow },
            new Release { ProjectId = _projectId, Version = "1.0.0", BranchName = "main", PublishedAt = DateTime.UtcNow.AddDays(-2) },
            new Release { ProjectId = _projectId, Version = "2.0.0", BranchName = "main", PublishedAt = DateTime.UtcNow.AddDays(-1) }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetReleasesPagedAsync(null, null, 1, 2, ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, total);
        Assert.Equal(2, items.Count);
        Assert.Equal("3.0.0", items[0].Version);
        Assert.Equal("2.0.0", items[1].Version);
    }

    [Fact]
    public async Task GetReleasesPagedAsync_UnpublishedReleasesSortLast()
    {
        // Regression: DetectedAt was never assigned, so every row tied at default(DateTime) and
        // the newest release never surfaced. Published releases must now lead; unpublished trail.
        _db.Releases.AddRange(
            new Release { ProjectId = _projectId, Version = "0.9.0", BranchName = "main", Status = ReleaseStatus.Detected, PublishedAt = null },
            new Release { ProjectId = _projectId, Version = "1.0.0", BranchName = "main", PublishedAt = DateTime.UtcNow }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetReleasesPagedAsync(null, null, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal("1.0.0", items[0].Version);
        Assert.Equal("0.9.0", items[^1].Version);
    }

    [Fact]
    public async Task GetReleasesPagedAsync_WithSearch_FiltersOnVersionAndBranch()
    {
        _db.Releases.AddRange(
            new Release { ProjectId = _projectId, Version = "1.0.0", BranchName = "main" },
            new Release { ProjectId = _projectId, Version = "2.0.0", BranchName = "release/2.0" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetReleasesPagedAsync("release", null, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, total);
        Assert.Equal("2.0.0", items[0].Version);
    }

    [Fact]
    public async Task GetReleasesPagedAsync_WithProjectId_Filters()
    {
        var otherProject = new Project { Name = "Other" };
        _db.Projects.Add(otherProject);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.Releases.AddRange(
            new Release { ProjectId = _projectId, Version = "1.0.0", BranchName = "main" },
            new Release { ProjectId = otherProject.Id, Version = "2.0.0", BranchName = "main" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetReleasesPagedAsync(null, _projectId, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task GetReleasesPagedAsync_WithAccessibleIds_Filters()
    {
        _db.Releases.AddRange(
            new Release { ProjectId = _projectId, Version = "1.0.0", BranchName = "main" },
            new Release { ProjectId = _projectId, Version = "2.0.0", BranchName = "main" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ids = await _db.Releases.Select(r => r.Id).Take(1).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        var (items, total) = await _repo.GetReleasesPagedAsync(null, null, 1, 10, ids, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task GetReleasesPagedAsync_IncludesLinkedArtifacts()
    {
        var release = new Release { ProjectId = _projectId, Version = "1.0.0", BranchName = "main" };
        release.Artifacts.Add(new PipelineArtifact
        {
            ProjectId = _projectId,
            Name = "validated-images",
            FilePath = "artifact.zip",
            CreatedAt = DateTime.UtcNow,
            RetentionExpiresAt = DateTime.UtcNow.AddDays(30)
        });
        _db.Releases.Add(release);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetReleasesPagedAsync(null, _projectId, 1, 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal("validated-images", Assert.Single(Assert.Single(items).Artifacts).Name);
    }

    [Fact]
    public async Task GetProjectReleasesAsync_ReturnsOrderedByPublishedAtDesc()
    {
        // Newest published release inserted first (lowest Id) so an Id-based fallback would fail.
        _db.Releases.AddRange(
            new Release { ProjectId = _projectId, Version = "2.0.0", BranchName = "main", PublishedAt = DateTime.UtcNow },
            new Release { ProjectId = _projectId, Version = "1.0.0", BranchName = "main", PublishedAt = DateTime.UtcNow.AddDays(-1) }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetProjectReleasesAsync(_projectId, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        Assert.Equal("2.0.0", result[0].Version);
    }

    [Fact]
    public async Task GetProjectReleasesAsync_IncludesLinkedArtifacts()
    {
        var release = new Release { ProjectId = _projectId, Version = "1.0.0", BranchName = "main" };
        release.Artifacts.Add(new PipelineArtifact
        {
            ProjectId = _projectId,
            Name = "validated-images",
            FilePath = "artifact.zip",
            CreatedAt = DateTime.UtcNow,
            RetentionExpiresAt = DateTime.UtcNow.AddDays(30)
        });
        _db.Releases.Add(release);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var items = await _repo.GetProjectReleasesAsync(_projectId, ct: TestContext.Current.CancellationToken);

        Assert.Equal("validated-images", Assert.Single(Assert.Single(items).Artifacts).Name);
    }

    [Fact]
    public async Task FindReleaseAsync_Found_IncludesProject()
    {
        var release = new Release { ProjectId = _projectId, Version = "1.0.0", BranchName = "main" };
        _db.Releases.Add(release);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindReleaseAsync(release.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.NotNull(result.Project);
    }

    [Fact]
    public async Task FindReleaseAsync_NotFound_ReturnsNull()
    {
        var result = await _repo.FindReleaseAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task FindByVersionAsync_Found()
    {
        _db.Releases.Add(new Release { ProjectId = _projectId, Version = "1.0.0", BranchName = "main" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindByVersionAsync(_projectId, "1.0.0", ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FindByVersionAsync_NotFound_ReturnsNull()
    {
        var result = await _repo.FindByVersionAsync(_projectId, "unknown", ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task AddReleaseAsync_Persists()
    {
        await _repo.AddReleaseAsync(new Release { ProjectId = _projectId, Version = "1.0.0", BranchName = "main" }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.Releases.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindByPipelineRunIdAsync_Found()
    {
        _db.Releases.Add(new Release { ProjectId = _projectId, Version = "1.0.0", BranchName = "main", PipelineRunId = 42 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindByPipelineRunIdAsync(42, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FindByPipelineRunIdAsync_NotFound_ReturnsNull()
    {
        var result = await _repo.FindByPipelineRunIdAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByPipelineRunIdAsync_KeepsTheReleaseACandidatePublished_AfterTheDeployRunRewroteIt()
    {
        // The candidate run published release 51, then the deploy run recorded it again and became
        // its PipelineRunId. The candidate's release step output still names it. A RELEASE_ID
        // pointing at another project's release, or written by a failed step, is ignored.
        _db.Pipelines.AddRange(
            new Pipeline { Id = 50, Name = "candidate", ProjectId = _projectId },
            new Pipeline { Id = 51, Name = "deploy-prod", ProjectId = _projectId });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 2421, PipelineId = 50 },
            new PipelineRun { Id = 2422, PipelineId = 51 });
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = 2421, StageName = "Candidate", StepName = "Publish", Status = TaskExecutionStatus.Success, OutputVariablesJson = "{\"RELEASE_ID\":\"51\"}" },
            new PipelineStepRun { PipelineRunId = 2421, StageName = "Other", StepName = "Spoof", Status = TaskExecutionStatus.Success, OutputVariablesJson = "{\"RELEASE_ID\":\"52\"}" },
            new PipelineStepRun { PipelineRunId = 2421, StageName = "Other", StepName = "Failed", Status = TaskExecutionStatus.Failed, OutputVariablesJson = "{\"RELEASE_ID\":\"53\"}" });
        _db.Releases.AddRange(
            new Release { Id = 51, ProjectId = _projectId, Version = "c-9e1d", Status = ReleaseStatus.Deployed, PipelineRunId = 2422 },
            new Release { Id = 52, ProjectId = _projectId + 1, Version = "c-other", Status = ReleaseStatus.Published },
            new Release { Id = 53, ProjectId = _projectId, Version = "c-failed", Status = ReleaseStatus.Published });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var candidate = await _repo.GetByPipelineRunIdAsync(2421, TestContext.Current.CancellationToken);
        var deploy = await _repo.GetByPipelineRunIdAsync(2422, TestContext.Current.CancellationToken);

        Assert.Equal(51, Assert.Single(candidate).Id);
        Assert.Equal(51, Assert.Single(deploy).Id);
    }

    [Fact]
    public async Task FindPreviousPublishedWithArtifactAsync_UsesOnlyTheSameDeploymentCohort()
    {
        var previousAccept = new Release
        {
            ProjectId = _projectId,
            Version = "1.0.0",
            BranchName = "main",
            Status = ReleaseStatus.Deployed,
            PublishedAt = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc)
        };
        var previousProd = new Release
        {
            ProjectId = _projectId,
            Version = "1.1.0",
            BranchName = "main",
            Status = ReleaseStatus.Deployed,
            PublishedAt = new DateTime(2026, 7, 2, 10, 0, 0, DateTimeKind.Utc)
        };
        var current = new Release
        {
            ProjectId = _projectId,
            Version = "2.0.0",
            BranchName = "main",
            PublishedAt = new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc)
        };
        _db.Releases.AddRange(previousAccept, previousProd, current);
        _db.PipelineArtifacts.AddRange(
            new PipelineArtifact { Name = "accept.zip", FilePath = "accept.zip", EnvironmentName = "accept", Releases = [previousAccept] },
            new PipelineArtifact { Name = "prod.zip", FilePath = "prod.zip", EnvironmentName = "prod", Releases = [previousProd] },
            new PipelineArtifact { Name = "current.zip", FilePath = "current.zip", EnvironmentName = "accept", Releases = [current] });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindPreviousPublishedWithArtifactAsync(current, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(previousAccept.Id, result.Id);
        Assert.Single(result.Artifacts);
        Assert.Equal("accept", result.Artifacts[0].EnvironmentName);
    }

    [Fact]
    public async Task FindPreviousPublishedWithArtifactAsync_MissingDeploymentCohort_RefusesRollback()
    {
        var current = new Release
        {
            ProjectId = _projectId,
            Version = "2.0.0",
            BranchName = "main",
            PublishedAt = new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc)
        };
        _db.Releases.Add(current);
        _db.PipelineArtifacts.Add(new PipelineArtifact
        {
            Name = "current.zip",
            FilePath = "current.zip",
            Releases = [current]
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindPreviousPublishedWithArtifactAsync(current, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindPreviousPublishedWithArtifactAsync_SkipsAReleaseThatWasRolledBack()
    {
        var previousGood = new Release
        {
            ProjectId = _projectId,
            Version = "1.0.0",
            BranchName = "main",
            Status = ReleaseStatus.Deployed,
            PublishedAt = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc)
        };
        var previousRolledBack = new Release
        {
            ProjectId = _projectId,
            Version = "1.1.0",
            BranchName = "main",
            Status = ReleaseStatus.RolledBack,
            PublishedAt = new DateTime(2026, 7, 2, 10, 0, 0, DateTimeKind.Utc)
        };
        var current = new Release
        {
            ProjectId = _projectId,
            Version = "2.0.0",
            BranchName = "main",
            Status = ReleaseStatus.Deployed,
            PublishedAt = new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc)
        };
        _db.Releases.AddRange(previousGood, previousRolledBack, current);
        _db.PipelineArtifacts.AddRange(
            new PipelineArtifact { Name = "good.zip", FilePath = "good.zip", EnvironmentName = "prod", Releases = [previousGood] },
            new PipelineArtifact { Name = "rolled-back.zip", FilePath = "rolled-back.zip", EnvironmentName = "prod", Releases = [previousRolledBack] },
            new PipelineArtifact { Name = "current.zip", FilePath = "current.zip", EnvironmentName = "prod", Releases = [current] });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindPreviousPublishedWithArtifactAsync(current, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(previousGood.Id, result.Id);
    }

    [Fact]
    public async Task SaveChangesAsync_PersistsPendingChanges()
    {
        _db.Releases.Add(new Release { ProjectId = _projectId, Version = "v", BranchName = "b" });
        await _repo.SaveChangesAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.Releases.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetReleasesPagedAsync_Deployable_KeepsEveryReleaseWhosePayloadIsRetained()
    {
        // Release restoration requires retained payload regardless of status; exclude purged
        // artifacts.
        PipelineArtifact Payload() => new() { Name = "application-payload", FilePath = "a.tgz" };
        _db.Releases.AddRange(
            new Release { ProjectId = _projectId, Version = "published-with-payload", BranchName = "develop", Status = ReleaseStatus.Published, Artifacts = [Payload()] },
            new Release { ProjectId = _projectId, Version = "deployed-with-payload", BranchName = "develop", Status = ReleaseStatus.Deployed, Artifacts = [Payload()] },
            new Release { ProjectId = _projectId, Version = "published-purged", BranchName = "develop", Status = ReleaseStatus.Published },
            new Release { ProjectId = _projectId, Version = "superseded-with-payload", BranchName = "develop", Status = ReleaseStatus.Superseded, Artifacts = [Payload()] },
            new Release { ProjectId = _projectId, Version = "failed-with-payload", BranchName = "develop", Status = ReleaseStatus.Failed, Artifacts = [Payload()] });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (deployable, total) = await _repo.GetReleasesPagedAsync(
            null, _projectId, 1, 25, ct: TestContext.Current.CancellationToken, deployableOnly: true);
        var (all, _) = await _repo.GetReleasesPagedAsync(null, _projectId, 1, 25, ct: TestContext.Current.CancellationToken);

        Assert.Equal(4, total);
        Assert.Equal(
            ["deployed-with-payload", "failed-with-payload", "published-with-payload", "superseded-with-payload"],
            deployable.Select(release => release.Version).Order());
        Assert.Equal(5, all.Count);
    }

    [Fact]
    public async Task GetRedeployTargetsAsync_NamesThePreviousDeploymentAndTheLivePipeline()
    {
        // The newest Superseded release is the one production ran before the live one, and the live
        // release points at the run that deployed it, launched with a candidateVersion.
        _db.Pipelines.Add(new Pipeline { Id = 40, Name = "deploy-prod" });
        _db.PipelineRuns.Add(new PipelineRun { Id = 400, PipelineId = 40, ParametersJson = "{\"candidateVersion\":\"c-3\"}" });
        _db.Releases.AddRange(
            new Release { Id = 1, ProjectId = _projectId, Version = "c-1", Status = ReleaseStatus.Superseded, PublishedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Release { Id = 2, ProjectId = _projectId, Version = "c-2", Status = ReleaseStatus.Superseded, PublishedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc) },
            new Release { Id = 3, ProjectId = _projectId, Version = "c-3", Status = ReleaseStatus.Deployed, PublishedAt = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc), PipelineRunId = 400 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var target = (await _repo.GetRedeployTargetsAsync([_projectId], TestContext.Current.CancellationToken))[_projectId];

        Assert.Equal(new ReleaseRedeployTarget(2, 40), target);
    }

    [Fact]
    public async Task GetRedeployTargetsAsync_NamesTheProjectsRevertPipeline_ByTheTemplateItExtends()
    {
        // Identify quick return through the host-bluegreen-revert template.
        _db.Pipelines.AddRange(
            new Pipeline { Id = 42, Name = "deploy-prod", ProjectId = _projectId, TemplateReferenceName = "host-bluegreen-deploy" },
            new Pipeline { Id = 43, Name = "revert-prod", ProjectId = _projectId, TemplateReferenceName = "host-bluegreen-revert" },
            new Pipeline { Id = 44, Name = "someone-elses-revert", ProjectId = _projectId + 1, TemplateReferenceName = "host-bluegreen-revert" });
        _db.Releases.AddRange(
            new Release { Id = 21, ProjectId = _projectId, Version = "c-1", Status = ReleaseStatus.Superseded, PublishedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Release { Id = 22, ProjectId = _projectId, Version = "c-2", Status = ReleaseStatus.Deployed, PublishedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var target = (await _repo.GetRedeployTargetsAsync([_projectId], TestContext.Current.CancellationToken))[_projectId];

        Assert.Equal(43, target.RevertPipelineId);
    }

    [Fact]
    public async Task GetRedeployTargetsAsync_WithoutACandidateVersionRunOffersNoPipeline()
    {
        // A live release deployed by a run that took no candidateVersion (release-fast) has no
        // pipeline that could redeploy the previous one by version.
        _db.Pipelines.Add(new Pipeline { Id = 41, Name = "release-fast" });
        _db.PipelineRuns.Add(new PipelineRun { Id = 410, PipelineId = 41 });
        _db.Releases.AddRange(
            new Release { Id = 11, ProjectId = _projectId, Version = "c-1", Status = ReleaseStatus.Superseded, PublishedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Release { Id = 12, ProjectId = _projectId, Version = "c-2", Status = ReleaseStatus.Deployed, PublishedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc), PipelineRunId = 410 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var target = (await _repo.GetRedeployTargetsAsync([_projectId], TestContext.Current.CancellationToken))[_projectId];

        Assert.Equal(new ReleaseRedeployTarget(11, null), target);
    }
}
