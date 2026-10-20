// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppBackups;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Which source run status lets a restore read the artifact of the run this pipeline
/// triggered. A Rejected run ran every control to the end and sealed what it found (recette R1-80), so
/// its artifacts are complete; a run that failed, stopped or went partial has none to trust. The two
/// lookups that name no triggered run stay Success-only.</summary>
public sealed class PipelineArtifactSourceStatusTests
{
    private const int RunId = 10;
    private const int SourceRunId = 20;
    private const int ProjectId = 7;
    private const string Commit = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0";
    private const string ArtifactName = "ApplicationPayload-artifacts";
    private const string SourcePipeline = "aetheus-ci";

    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IArtifactRepository _artifacts = Substitute.For<IArtifactRepository>();
    private readonly PipelineArtifactTaskFactory _sut;
    private readonly PipelineArtifact _artifact = new() { Id = 5, PipelineRunId = SourceRunId, ProjectId = ProjectId };

    public PipelineArtifactSourceStatusTests()
    {
        var currentPipeline = new Pipeline { Id = 1, Name = "aetheus-quality", ProjectId = ProjectId };
        _repo.GetPipelineRunWithPipelineAsync(RunId, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = RunId, Pipeline = currentPipeline, CommitHash = Commit });
        _repo.GetPipelineProjectIdAsync(currentPipeline, Arg.Any<CancellationToken>()).Returns(ProjectId);
        _repo.FindTriggeredRunIdByPipelineNameAsync(RunId, SourcePipeline, Arg.Any<CancellationToken>()).Returns(SourceRunId);
        _artifacts.FindRunArtifactByNameAsync(SourceRunId, ArtifactName, Arg.Any<CancellationToken>()).Returns(_artifact);
        _sut = new PipelineArtifactTaskFactory(
            _repo, _artifacts, Substitute.For<IEncryptionService>(), new FakeTimeProvider(), Substitute.For<IBackupRepository>());
    }

    private void SourceRun(PipelineStatus status, string commit = Commit)
    {
        var pipeline = new Pipeline { Id = 2, Name = SourcePipeline, ProjectId = ProjectId };
        _repo.GetPipelineRunWithPipelineAsync(SourceRunId, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = SourceRunId, Pipeline = pipeline, CommitHash = commit, Status = status });
        _repo.GetPipelineProjectIdAsync(pipeline, Arg.Any<CancellationToken>()).Returns(ProjectId);
    }

    private Task<(PipelineArtifact? Artifact, string? Error)> ResolveAsync(string? selector = null) =>
        _sut.ResolveArtifactSourceAsync(
            RunId,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new PipelineStepDefinition
            {
                Name = "restore",
                Artifact = ArtifactName,
                ArtifactSourcePipeline = SourcePipeline,
                ArtifactSourceSelector = selector
            },
            TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(PipelineStatus.Success)]
    [InlineData(PipelineStatus.Rejected)]
    public async Task TheTriggeredRun_WhenItSucceededOrWasRejected_GivesItsArtifact(PipelineStatus status)
    {
        SourceRun(status);

        var (artifact, error) = await ResolveAsync();

        Assert.Same(_artifact, artifact);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(PipelineStatus.Failed)]
    [InlineData(PipelineStatus.Partial)]
    [InlineData(PipelineStatus.Cancelled)]
    [InlineData(PipelineStatus.RolledBack)]
    [InlineData(PipelineStatus.Running)]
    [InlineData(PipelineStatus.Pending)]
    public async Task TheTriggeredRun_WhenItDidNotComplete_GivesNothing(PipelineStatus status)
    {
        SourceRun(status);

        var (artifact, error) = await ResolveAsync();

        Assert.Null(artifact);
        Assert.Contains("optional producer artifact is unavailable", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedTriggeredRun_OnAnotherCommit_IsStillRefused()
    {
        SourceRun(PipelineStatus.Rejected, commit: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        var (artifact, error) = await ResolveAsync();

        Assert.Null(artifact);
        Assert.Equal("the source run belongs to another source commit.", error);
    }

    [Fact]
    public async Task LatestSuccessful_NeverReadsTheTriggeredRun_SoARejectedOneIsNotAccepted()
    {
        SourceRun(PipelineStatus.Rejected);
        _artifacts.FindLatestSuccessfulPipelineArtifactAsync(ProjectId, SourcePipeline, ArtifactName, Arg.Any<CancellationToken>())
            .Returns((PipelineArtifact?)null);

        var (artifact, error) = await ResolveAsync(PipelineArtifactTaskFactory.LatestSuccessfulSelector);

        Assert.Null(artifact);
        Assert.Contains("never produced by a successful", error, StringComparison.Ordinal);
        await _artifacts.DidNotReceive().FindRunArtifactByNameAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutATriggeredRun_TheSameCommitLookupStaysSuccessOnly()
    {
        _repo.FindTriggeredRunIdByPipelineNameAsync(RunId, SourcePipeline, Arg.Any<CancellationToken>()).Returns((int?)null);
        _artifacts.FindSuccessfulPipelineArtifactByCommitAsync(
            ProjectId, SourcePipeline, Commit, ArtifactName, Arg.Any<CancellationToken>()).Returns((PipelineArtifact?)null);

        var (artifact, error) = await ResolveAsync();

        Assert.Null(artifact);
        Assert.Contains("successful aetheus-ci run", error, StringComparison.Ordinal);
    }
}
