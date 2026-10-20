// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppBackups;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.GitGraph;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Releases;

/// <summary>
/// The release lifecycle signals that arrive after a pipeline finishes, plus the rollback preview.
/// The preview is the interesting one: it must refuse to promise a rollback whose artifact is no
/// longer in storage, because a rollback that cannot restore anything is worse than none.
/// </summary>
public sealed class ReleaseServiceLifecycleTests
{
    private static readonly DateTime NowUtc = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IReleaseRepository _repository = Substitute.For<IReleaseRepository>();
    private readonly IArtifactStorageService _artifactStorage = Substitute.For<IArtifactStorageService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IHubContext<ReleaseHub> _releaseHub = Substitute.For<IHubContext<ReleaseHub>>();
    private readonly IPipelineRepository _pipelineRepository = Substitute.For<IPipelineRepository>();
    private readonly IArtifactRepository _artifactRepository = Substitute.For<IArtifactRepository>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(NowUtc));
    private readonly ReleaseService _service;

    public ReleaseServiceLifecycleTests()
    {
        _pipelineRepository.GetRootRunReferencesAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, PipelineRunRootReference>());
        _releaseHub.Clients.Groups(Arg.Any<IReadOnlyList<string>>())
            .Returns(Substitute.For<IClientProxy>());
        // The real repository returns a list; an unstubbed substitute returns null, and the retention
        // loop would blame the service for the harness.
        _artifactRepository.GetByRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _service = new ReleaseService(
            _repository,
            Substitute.For<IProjectService>(),
            Substitute.For<IGitCliService>(),
            Substitute.For<IPipelineLauncher>(),
            Substitute.For<IPipelineService>(),
            _releaseHub,
            Substitute.For<IGitGraphRecorder>(),
            _clock,
            _artifactStorage,
            Substitute.For<IBackupRepository>(),
            _audit,
            _artifactRepository,
            Substitute.For<IArtifactRetentionService>(),
            _pipelineRepository,
            Substitute.For<IDbTransactionScope>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Release Release(
        int id = 1,
        string version = "1.0.0",
        ReleaseStatus status = ReleaseStatus.Published,
        int projectId = 10,
        int? pipelineRunId = null,
        params PipelineArtifact[] artifacts) =>
        new()
        {
            Id = id,
            ProjectId = projectId,
            Version = version,
            BranchName = "main",
            Status = status,
            PipelineRunId = pipelineRunId,
            PublishedAt = NowUtc.AddDays(-1),
            Artifacts = [.. artifacts]
        };

    private static PipelineArtifact Artifact(int id, string filePath, DateTime createdAt) =>
        new()
        {
            Id = id,
            PipelineId = 1,
            PipelineRunId = 5,
            Name = $"artifact-{id}",
            FilePath = filePath,
            CreatedAt = createdAt
        };

    // ---------- rollback preview ----------

    [Fact]
    public async Task GetRollbackPreviewAsync_RefusesAnUnknownRelease()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _service.GetRollbackPreviewAsync(404, Ct));
    }

    [Fact]
    public async Task GetRollbackPreviewAsync_ExplainsThereIsNoPreviousReleaseToGoBackTo()
    {
        _repository.FindReleaseAsync(1, Arg.Any<CancellationToken>()).Returns(Release());

        var preview = await _service.GetRollbackPreviewAsync(1, Ct);

        Assert.False(preview.CanRollback);
        Assert.Contains("No previous release", preview.Reason!, StringComparison.Ordinal);
        Assert.Null(preview.TargetVersion);
    }

    [Fact]
    public async Task GetRollbackPreviewAsync_RefusesWhenTheTargetArtifactLeftStorage()
    {
        var target = Release(2, "0.9.0", artifacts: Artifact(1, "releases/0.9.0.zip", NowUtc));
        _repository.FindReleaseAsync(1, Arg.Any<CancellationToken>()).Returns(Release());
        _repository.FindPreviousPublishedWithArtifactAsync(
            Arg.Any<Release>(), Arg.Any<CancellationToken>()).Returns(target);

        var preview = await _service.GetRollbackPreviewAsync(1, Ct);

        Assert.False(preview.CanRollback);
        Assert.Contains("no longer available", preview.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRollbackPreviewAsync_AnnouncesTheNewestRetainedArtifactAsTheTarget()
    {
        var target = Release(
            2,
            "0.9.0",
            artifacts:
            [
                Artifact(1, "releases/old.zip", NowUtc.AddDays(-2)),
                Artifact(2, "releases/newest.zip", NowUtc)
            ]);
        _repository.FindReleaseAsync(1, Arg.Any<CancellationToken>()).Returns(Release());
        _repository.FindPreviousPublishedWithArtifactAsync(
            Arg.Any<Release>(), Arg.Any<CancellationToken>()).Returns(target);
        _artifactStorage.OpenArtifact("releases/newest.zip").Returns(new MemoryStream([1, 2, 3]));

        var preview = await _service.GetRollbackPreviewAsync(1, Ct);

        Assert.True(preview.CanRollback);
        Assert.Equal("0.9.0", preview.TargetVersion);
        Assert.Equal(target.PublishedAt, preview.TargetPublishedAt);
        Assert.Null(preview.Reason);
    }

    // ---------- single release and per-run reads ----------

    [Fact]
    public async Task GetReleaseAsync_ReturnsNothingForAnUnknownRelease()
    {
        Assert.Null(await _service.GetReleaseAsync(404, Ct));
    }

    [Fact]
    public async Task GetReleaseAsync_MapsTheKnownRelease()
    {
        _repository.FindReleaseAsync(1, Arg.Any<CancellationToken>()).Returns(Release(pipelineRunId: 5));

        var release = await _service.GetReleaseAsync(1, Ct);

        Assert.Equal("1.0.0", release!.Version);
        Assert.Equal(10, release.ProjectId);
        Assert.Equal(ReleaseStatus.Published, release.Status);
    }

    [Fact]
    public async Task GetReleasesByRunAsync_MapsEveryReleaseProducedByThatRun()
    {
        _repository.GetByPipelineRunIdAsync(5, Arg.Any<CancellationToken>())
            .Returns([Release(1, "1.0.0", pipelineRunId: 5), Release(2, "1.0.1", pipelineRunId: 5)]);

        var releases = await _service.GetReleasesByRunAsync(5, Ct);

        Assert.Equal(["1.0.0", "1.0.1"], releases.Select(release => release.Version));
    }

    [Fact]
    public async Task GetReleasesByRunAsync_IsEmptyForARunThatProducedNothing()
    {
        _repository.GetByPipelineRunIdAsync(5, Arg.Any<CancellationToken>()).Returns([]);

        Assert.Empty(await _service.GetReleasesByRunAsync(5, Ct));
    }

    /// <summary>Published releases record their detection time instead of retaining an unset
    /// timestamp.</summary>
    [Fact]
    public async Task CreateReleaseFromPipelineAsync_DatesTheReleaseItCreates()
    {
        Release? added = null;
        await _repository.AddReleaseAsync(Arg.Do<Release>(release => added = release), Arg.Any<CancellationToken>());

        await _service.CreateReleaseFromPipelineAsync(1, 5, "c-384f63d4a8c182e07de2e9444c25e0a5a9a28e8e", null, ct: Ct);

        Assert.NotNull(added);
        Assert.Equal(NowUtc, added.DetectedAt);
        Assert.Equal(NowUtc, added.PublishedAt);
    }

    // ---------- pipeline completion ----------

    [Fact]
    public async Task NotifyPipelineRunCompletedAsync_IsANoOpWhenNoReleaseIsAttachedToTheRun()
    {
        await _service.NotifyPipelineRunCompletedAsync(5, PipelineStatus.Success, Ct);

        await _repository.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(PipelineStatus.Success, ReleaseStatus.Published)]
    [InlineData(PipelineStatus.Failed, ReleaseStatus.Failed)]
    [InlineData(PipelineStatus.Cancelled, ReleaseStatus.Failed)]
    public async Task NotifyPipelineRunCompletedAsync_MovesTheReleaseToTheOutcomeOfItsRun(
        PipelineStatus status, ReleaseStatus expected)
    {
        var release = Release(status: ReleaseStatus.Detected, pipelineRunId: 5);
        _repository.FindByPipelineRunIdAsync(5, Arg.Any<CancellationToken>()).Returns(release);

        await _service.NotifyPipelineRunCompletedAsync(5, status, Ct);

        Assert.Equal(expected, release.Status);
        Assert.Equal(expected == ReleaseStatus.Published ? NowUtc : NowUtc.AddDays(-1), release.PublishedAt);
        await _repository.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>A failed deployment attempt must not change a previously published release into
    /// Failed; only releases still being built take that outcome.</summary>
    [Theory]
    [InlineData(ReleaseStatus.Published, PipelineStatus.Failed)]
    [InlineData(ReleaseStatus.Published, PipelineStatus.Cancelled)]
    [InlineData(ReleaseStatus.Superseded, PipelineStatus.Failed)]
    public async Task NotifyPipelineRunCompletedAsync_AFailedDeploymentLeavesAPublishedReleaseAsItWas(
        ReleaseStatus published, PipelineStatus outcome)
    {
        var release = Release(status: published, pipelineRunId: 5);
        var publishedAt = release.PublishedAt;
        _repository.FindByPipelineRunIdAsync(5, Arg.Any<CancellationToken>()).Returns(release);

        await _service.NotifyPipelineRunCompletedAsync(5, outcome, Ct);

        Assert.Equal(published, release.Status);
        Assert.Equal(publishedAt, release.PublishedAt);
    }

    [Fact]
    public async Task NotifyPipelineRunCompletedAsync_NeverDemotesAnAlreadyDeployedRelease()
    {
        var release = Release(status: ReleaseStatus.Deployed, pipelineRunId: 5);
        _repository.FindByPipelineRunIdAsync(5, Arg.Any<CancellationToken>()).Returns(release);

        await _service.NotifyPipelineRunCompletedAsync(5, PipelineStatus.Failed, Ct);

        Assert.Equal(ReleaseStatus.Deployed, release.Status);
    }

    [Theory]
    [InlineData(PipelineStatus.Success, "without a successful deployment task")]
    [InlineData(PipelineStatus.Failed, "before the deployment health gate succeeded")]
    public async Task NotifyPipelineRunCompletedAsync_FailsAPendingRollbackWithAnExplicitReason(
        PipelineStatus status, string expectedReason)
    {
        var rollback = Rollback();
        _repository.FindRollbackByPipelineRunIdAsync(5, Arg.Any<CancellationToken>()).Returns(rollback);

        await _service.NotifyPipelineRunCompletedAsync(5, status, Ct);

        Assert.Equal(RollbackStatus.Failed, rollback.Status);
        Assert.Equal(NowUtc, rollback.CompletedAt);
        Assert.Contains(expectedReason, rollback.FailureReason!, StringComparison.Ordinal);
        await _audit.Received().LogAsync(
            "RollbackFailed", "Release", rollback.SourceReleaseId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().FindByPipelineRunIdAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyPipelineRunCompletedAsync_LeavesARollbackThatAlreadyFinishedAlone()
    {
        var rollback = Rollback(RollbackStatus.Succeeded);
        _repository.FindRollbackByPipelineRunIdAsync(5, Arg.Any<CancellationToken>()).Returns(rollback);

        await _service.NotifyPipelineRunCompletedAsync(5, PipelineStatus.Failed, Ct);

        Assert.Equal(RollbackStatus.Succeeded, rollback.Status);
        Assert.Null(rollback.FailureReason);
    }

    // ---------- rollback success ----------

    [Fact]
    public async Task NotifyRollbackDeploymentSucceededAsync_SwapsTheTwoReleaseStatuses()
    {
        var rollback = Rollback();
        _repository.FindRollbackAsync(1, Arg.Any<CancellationToken>()).Returns(rollback);

        await _service.NotifyRollbackDeploymentSucceededAsync(1, Ct);

        Assert.Equal(RollbackStatus.Succeeded, rollback.Status);
        Assert.Equal(NowUtc, rollback.CompletedAt);
        Assert.Equal(ReleaseStatus.RolledBack, rollback.SourceRelease.Status);
        Assert.Equal(NowUtc, rollback.SourceRelease.RolledBackAt);
        Assert.Equal(ReleaseStatus.Deployed, rollback.TargetRelease.Status);
        await _audit.Received().LogAsync(
            "RollbackSucceeded", "Release", rollback.SourceReleaseId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyRollbackDeploymentSucceededAsync_IgnoresAnUnknownOrFinishedRollback()
    {
        await _service.NotifyRollbackDeploymentSucceededAsync(404, Ct);

        var finished = Rollback(RollbackStatus.Failed);
        _repository.FindRollbackAsync(2, Arg.Any<CancellationToken>()).Returns(finished);
        await _service.NotifyRollbackDeploymentSucceededAsync(2, Ct);

        Assert.Equal(RollbackStatus.Failed, finished.Status);
        await _repository.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ---------- webhook signature ----------

    [Fact]
    public void ValidateWebhookSignature_RefusesAnAbsentOrWrongSignature()
    {
        Assert.False(_service.ValidateWebhookSignature(null, "secret", "{}"));
        Assert.False(_service.ValidateWebhookSignature("sha256=deadbeef", "secret", "{}"));
    }

    private static ReleaseRollback Rollback(RollbackStatus status = RollbackStatus.Pending) =>
        new()
        {
            Id = 1,
            SourceReleaseId = 1,
            TargetReleaseId = 2,
            PipelineId = 3,
            PipelineRunId = 5,
            Status = status,
            RequestedAt = NowUtc.AddMinutes(-5),
            SourceRelease = Release(1, "1.0.0", ReleaseStatus.Deployed),
            TargetRelease = Release(2, "0.9.0")
        };

    // Rollback can demote the release deployed by its own attempt.

    [Fact]
    public async Task RecordingAnAlreadyDeployedVersionAsNotDeployed_DemotesIt()
    {
        // Without this, a compensating Rollback stage could restore the previous colour and leave the
        // database saying the version it just undid is the deployed one, which is the exact state
        // prod-deploy-failure-modes.md describes as needing a manual fix.
        // Deployed by this very run (its Record release step), then undone by its Rollback stage.
        var deployed = Release(7, "1.2.3", ReleaseStatus.Deployed, pipelineRunId: 5);
        _repository.FindByVersionAsync(10, "1.2.3", Arg.Any<CancellationToken>()).Returns(deployed);

        await _service.CreateReleaseFromPipelineAsync(
            projectId: 10, pipelineRunId: 5, version: "1.2.3", changelog: null, deployed: false, ct: Ct);

        Assert.Equal(ReleaseStatus.Published, deployed.Status);
    }

    /// <summary>
    /// Deploy-prod 2369 redeployed the live release, was not confirmed, and its Rollback stage put
    /// traffic back on the colour that release was already serving - then demoted it, leaving no
    /// release Deployed while it ran production. A release a previous run deployed is still the one
    /// the rollback returns to: this run did not deploy it, so it cannot undo it.
    /// </summary>
    [Fact]
    public async Task RecordingAsNotDeployed_LeavesAReleaseAnEarlierRunDeployed()
    {
        var live = Release(7, "1.2.3", ReleaseStatus.Deployed, pipelineRunId: 2368);
        _repository.FindByVersionAsync(10, "1.2.3", Arg.Any<CancellationToken>()).Returns(live);

        await _service.CreateReleaseFromPipelineAsync(
            projectId: 10, pipelineRunId: 2369, version: "1.2.3", changelog: null, deployed: false, ct: Ct);

        Assert.Equal(ReleaseStatus.Deployed, live.Status);
        Assert.Equal(2368, live.PipelineRunId);
    }

    [Fact]
    public async Task RecordingAsNotDeployed_DoesNotSupersedeTheOtherDeployedReleases()
    {
        // Superseding is what a real deployment does to its predecessor. A demotion is the opposite
        // act, and must not take the rest of the project's history down with it.
        var deployed = Release(7, "1.2.3", ReleaseStatus.Deployed);
        _repository.FindByVersionAsync(10, "1.2.3", Arg.Any<CancellationToken>()).Returns(deployed);

        await _service.CreateReleaseFromPipelineAsync(
            projectId: 10, pipelineRunId: 5, version: "1.2.3", changelog: null, deployed: false, ct: Ct);

        await _repository.DidNotReceive().GetDeployedProjectReleasesAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
