// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Releases;

/// <summary> The run that created a release is recorded once and never moved by a later run, and
/// the provenance view keeps creation, later uses and build inputs apart. </summary>
public sealed class ReleaseProvenanceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private readonly AppDbContext _db;
    private readonly ReleaseRepository _releases;
    private readonly ReleaseProvenanceService _service;

    public ReleaseProvenanceTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _releases = new ReleaseRepository(_db);
        _service = new ReleaseProvenanceService(new ReleaseProvenanceRepository(_db));
        _db.Projects.AddRange(new Project { Id = 1, Name = "app" }, new Project { Id = 2, Name = "other" });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FirstPublication_StampsTheCreator_AndALaterRecordingRunNeverMovesIt()
    {
        var release = new Release { ProjectId = 1, Version = "1.0.0", PipelineRunId = 5, PublishedAt = T0 };
        await _releases.AddReleaseAsync(release, Ct);
        Assert.Equal(5, release.CreatedByPipelineRunId);

        // A deployment records the same release again (ReleaseService's existing-version path).
        release.PipelineRunId = 9;
        release.PublishedAt = T0.AddHours(1);
        await _releases.SaveChangesAsync(Ct);

        Assert.Equal(5, (await _db.Releases.AsNoTracking().SingleAsync(Ct)).CreatedByPipelineRunId);
        Assert.Equal(9, (await _db.Releases.AsNoTracking().SingleAsync(Ct)).PipelineRunId);
    }

    [Fact]
    public async Task DetectedRelease_IsStampedByTheRunThatFirstPublishesIt_NotByTheBuildTrigger()
    {
        var release = new Release { ProjectId = 1, Version = "2.0.0", Status = ReleaseStatus.Detected, DetectedAt = T0 };
        await _releases.AddReleaseAsync(release, Ct);
        Assert.Null(release.CreatedByPipelineRunId);

        release.PipelineRunId = 7; // TriggerReleaseBuildAsync: a build is launched, nothing published yet.
        await _releases.SaveChangesAsync(Ct);
        Assert.Null(release.CreatedByPipelineRunId);

        release.PublishedAt = T0.AddHours(2);
        await _releases.SaveChangesAsync(Ct);
        Assert.Equal(7, release.CreatedByPipelineRunId);
    }

    [Fact]
    public async Task ReleasePublishedBeforeTheTracking_IsNotClaimedByALaterDeployment()
    {
        // Seeded around the repository, as a row written before the column existed.
        _db.Releases.Add(new Release { Id = 30, ProjectId = 1, Version = "0.9.0", PipelineRunId = 3, PublishedAt = T0 });
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();

        var legacy = await _releases.FindByVersionAsync(1, "0.9.0", Ct);
        legacy!.PipelineRunId = 4;
        legacy.PublishedAt = T0.AddDays(1);
        await _releases.SaveChangesAsync(Ct);

        Assert.Null((await _db.Releases.AsNoTracking().SingleAsync(r => r.Id == 30, Ct)).CreatedByPipelineRunId);
    }

    [Fact]
    public async Task Provenance_SeparatesCreator_BuildInputs_AndLaterUses()
    {
        SeedDeliveryChain(creatorRunId: 100, lastRecordingRunId: 203);

        var provenance = await _service.GetProvenanceAsync(10, Ct);

        Assert.NotNull(provenance);
        Assert.Equal(100, provenance.CreatedBy!.RunId);
        Assert.Equal("candidate", provenance.CreatedBy.PipelineName);
        Assert.Null(provenance.LastRecordedBy);
        Assert.Equal(99, Assert.Single(provenance.BuildRuns).RunId);

        // Uses: never the creator, the producer or the build's child run; one entry per run, the
        // rollback outranking the deploy it also performed.
        Assert.Equal(
            [(200, ReleaseUseKind.Deploy), (201, ReleaseUseKind.Restore), (202, ReleaseUseKind.Rollback), (203, ReleaseUseKind.Recorded)],
            provenance.Uses.Select(use => (use.Run.RunId, use.Kind)).OrderBy(use => use.RunId));

        // Build inputs: what the creator and its triggered child consumed, not what uses consumed.
        Assert.Equal([100, 101], provenance.ArtifactInputs.Select(input => input.ConsumerRunId));
        Assert.True(provenance.ArtifactInputs[0].IsDeliverable);
        Assert.False(provenance.ArtifactInputs[1].IsDeliverable);
        Assert.Equal("base-image", provenance.ArtifactInputs[1].ArtifactName);
        Assert.Equal(50, provenance.ArtifactInputs[1].SourcePipelineRunId);

        // Packages: the build's SBOMs only, one row per package, direct first.
        Assert.Equal(2, provenance.PackageTotalCount);
        Assert.Equal(["Newtonsoft.Json", "Serilog"], provenance.Packages.Select(package => package.Name));
        Assert.True(provenance.Packages[0].IsDirect);
        Assert.Equal(100, provenance.Packages[0].PipelineRunId);
    }

    [Fact]
    public async Task Provenance_OfAReleaseWithoutRecordedCreator_ShowsItsLastRecordingRunApart()
    {
        SeedDeliveryChain(creatorRunId: null, lastRecordingRunId: 203);

        var provenance = await _service.GetProvenanceAsync(10, Ct);

        Assert.NotNull(provenance);
        Assert.Null(provenance.CreatedBy);
        Assert.Equal(203, provenance.LastRecordedBy!.RunId);
        Assert.DoesNotContain(provenance.Uses, use => use.Run.RunId == 203);
        // Without the creator the build is the producer run alone: the candidate's inputs are unknown.
        Assert.Empty(provenance.ArtifactInputs);
    }

    [Fact]
    public async Task Provenance_NeverExposesAnotherProjectsRuns()
    {
        SeedDeliveryChain(creatorRunId: 100, lastRecordingRunId: 203);
        // Another project's pipeline restored the deliverable, and was started by the creator's
        // trigger step: neither run, nor what it consumed, belongs in this project's provenance.
        _db.Pipelines.Add(new Pipeline { Id = 5, Name = "foreign", ProjectId = 2 });
        _db.PipelineRuns.AddRange(Run(300, 5, 8), Run(301, 5, 9));
        _db.PipelineStepRuns.Add(new PipelineStepRun
        {
            Id = 2,
            PipelineRunId = 100,
            StageName = "qa",
            StepName = "foreign",
            TriggeredRunId = 301
        });
        _db.PipelineRunArtifactInputs.AddRange(
            Input(6, 300, ArtifactInputKind.Restore, 1, "app", 99, null),
            Input(7, 301, ArtifactInputKind.Restore, 2, "base-image", 50, null));
        _db.SaveChanges();

        var provenance = await _service.GetProvenanceAsync(10, Ct);

        Assert.NotNull(provenance);
        Assert.DoesNotContain(provenance.Uses, use => use.Run.PipelineName == "foreign");
        Assert.DoesNotContain(provenance.BuildRuns, run => run.PipelineName == "foreign");
        Assert.Equal([100, 101], provenance.ArtifactInputs.Select(input => input.ConsumerRunId));
    }

    [Fact]
    public async Task Provenance_OfAnUnknownRelease_IsNull() =>
        Assert.Null(await _service.GetProvenanceAsync(404, Ct));

    private void SeedDeliveryChain(int? creatorRunId, int lastRecordingRunId)
    {
        _db.Pipelines.AddRange(
            new Pipeline { Id = 1, Name = "build", ProjectId = 1 },
            new Pipeline { Id = 2, Name = "candidate", ProjectId = 1 },
            new Pipeline { Id = 3, Name = "deploy-prod", ProjectId = 1 },
            new Pipeline { Id = 4, Name = "security", ProjectId = 1 });
        _db.PipelineRuns.AddRange(
            Run(50, 1, 0), Run(99, 1, 1), Run(100, 2, 2), Run(101, 4, 3),
            Run(200, 3, 4), Run(201, 3, 5), Run(202, 3, 6), Run(203, 3, 7));
        _db.PipelineStepRuns.Add(new PipelineStepRun
        {
            Id = 1,
            PipelineRunId = 100,
            StageName = "qa",
            StepName = "security",
            TriggeredRunId = 101
        });
        var deliverable = new PipelineArtifact { Id = 1, PipelineRunId = 99, PipelineId = 1, ProjectId = 1, Name = "app", Sha256 = new string('b', 64) };
        _db.PipelineArtifacts.AddRange(
            deliverable,
            new PipelineArtifact { Id = 2, PipelineRunId = 50, PipelineId = 1, ProjectId = 1, Name = "base-image" });
        _db.Releases.Add(new Release
        {
            Id = 10,
            ProjectId = 1,
            Version = "c-1234abcd",
            PublishedAt = T0,
            PipelineRunId = lastRecordingRunId,
            CreatedByPipelineRunId = creatorRunId,
            Artifacts = [deliverable]
        });
        _db.ReleaseRollbacks.Add(new ReleaseRollback
        {
            Id = 1,
            SourceReleaseId = 10,
            TargetReleaseId = 10,
            PipelineId = 3,
            PipelineRunId = 202,
            RequestedAt = T0
        });
        _db.PipelineRunArtifactInputs.AddRange(
            Input(1, 100, ArtifactInputKind.Restore, 1, "app", 99, null),
            Input(2, 101, ArtifactInputKind.Restore, 2, "base-image", 50, null),
            Input(3, 200, ArtifactInputKind.Deploy, 1, "app", 99, 10),
            Input(4, 201, ArtifactInputKind.Restore, 1, "app", 99, null),
            Input(5, 202, ArtifactInputKind.Deploy, 1, "app", 99, 10));
        _db.AnalysisReports.AddRange(
            Report(1, 1, 100), Report(2, 1, 101), Report(3, 1, 200), Report(4, 2, 100));
        _db.AnalysisComponents.AddRange(
            Component(1, 1, 1, "Serilog", false),
            Component(2, 1, 1, "Newtonsoft.Json", true),
            Component(3, 1, 2, "Serilog", false),      // the child run reports it again: one row
            Component(4, 1, 3, "Deployed.Only", true), // a use's SBOM, not the build's
            Component(5, 2, 4, "Other.Project", true)); // another project's report on the same run id
        _db.SaveChanges();
    }

    private static PipelineRun Run(int id, int pipelineId, int minutes) => new()
    {
        Id = id,
        PipelineId = pipelineId,
        Status = PipelineStatus.Success,
        StartedAt = T0.AddMinutes(minutes)
    };

    private static PipelineRunArtifactInput Input(
        int id, int runId, ArtifactInputKind kind, int artifactId, string name, int sourceRunId, int? releaseId) => new()
        {
            Id = id,
            PipelineRunId = runId,
            StepName = "step",
            Kind = kind,
            ArtifactId = artifactId,
            ArtifactName = name,
            SourcePipelineRunId = sourceRunId,
            ReleaseId = releaseId,
            RecordedAt = T0.AddMinutes(id)
        };

    private static AnalysisReport Report(int id, int projectId, int runId) => new()
    {
        Id = id,
        OrganizationId = 1,
        ProjectId = projectId,
        PipelineRunId = runId,
        ScannerKey = "syft",
        ScannerName = "Syft",
        ScannerVersion = "1",
        ContentHash = "h" + id
    };

    private static AnalysisComponent Component(int id, int projectId, int reportId, string name, bool direct) => new()
    {
        Id = id,
        OrganizationId = 1,
        ProjectId = projectId,
        AnalysisReportId = reportId,
        Name = name,
        Version = "1.0.0",
        PackageUrl = $"pkg:nuget/{name}@1.0.0",
        IsDirect = direct
    };
}
