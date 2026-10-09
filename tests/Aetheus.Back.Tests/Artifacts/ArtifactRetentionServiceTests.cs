// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Aetheus.Back.Tests.Artifacts;

public class ArtifactRetentionServiceTests
{
    private readonly IArtifactRepository _repo = Substitute.For<IArtifactRepository>();
    private readonly TimeProvider _time = Substitute.For<TimeProvider>();
    private readonly ArtifactRetentionService _sut;

    public ArtifactRetentionServiceTests()
    {
        _time.GetUtcNow().Returns(new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero));
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        _sut = new ArtifactRetentionService(_repo, config, _time);
    }

    [Fact]
    public async Task ApplyBuildRetentionAsync_SetsDefaultRetention()
    {
        var artifact = new PipelineArtifact { Id = 1, PipelineId = 1, ProjectId = 1 };
        _repo.GetByPipelineAndProjectAsync(1, 1, ArtifactRetentionPolicy.Build, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineArtifact>());

        await _sut.ApplyBuildRetentionAsync(artifact, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ArtifactRetentionPolicy.Build, artifact.RetentionPolicy);
        Assert.True(artifact.RetentionExpiresAt > DateTime.MinValue);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyBuildAndReleaseRetention_NullProjectId_DoNotThrow()
    {
        // BACK-009: a project-less artifact used to NRE on ProjectId!.Value after it was stored.
        var build = new PipelineArtifact { Id = 1, PipelineId = 1, ProjectId = null };
        var released = new PipelineArtifact { Id = 2, PipelineId = 1, ProjectId = null };

        await _sut.ApplyBuildRetentionAsync(build, ct: TestContext.Current.CancellationToken);
        await _sut.ApplyReleaseRetentionAsync(released, 8, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ArtifactRetentionPolicy.Build, build.RetentionPolicy);
        Assert.Equal(ArtifactRetentionPolicy.Released, released.RetentionPolicy);
        Assert.True(build.RetentionExpiresAt > DateTime.MinValue);
        await _repo.Received(1).LinkReleaseAsync(released, 8, Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().GetProjectRetentionOverridesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyDeployRetentionAsync_UpgradesPolicy()
    {
        var artifact = new PipelineArtifact { Id = 1, PipelineId = 1, ProjectId = 1, RetentionPolicy = ArtifactRetentionPolicy.Build };
        _repo.GetByEnvironmentAsync(1, 1, "prod", Arg.Any<CancellationToken>())
            .Returns(new List<PipelineArtifact>());

        await _sut.ApplyDeployRetentionAsync(artifact, "prod", ct: TestContext.Current.CancellationToken);

        Assert.Equal(ArtifactRetentionPolicy.Deployed, artifact.RetentionPolicy);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyDeployRetentionAsync_NullProjectId_DoesNotThrow_AndFlagsDeployed()
    {
        // Regression: a project-less artifact (orphan run) used to NRE on artifact.ProjectId!.Value.
        // The guard now flags it Deployed with the global-latest window and never dereferences null.
        var artifact = new PipelineArtifact { Id = 1, PipelineId = 1, ProjectId = null, RetentionPolicy = ArtifactRetentionPolicy.Build };

        await _sut.ApplyDeployRetentionAsync(artifact, "prod", ct: TestContext.Current.CancellationToken);

        Assert.Equal(ArtifactRetentionPolicy.Deployed, artifact.RetentionPolicy);
        Assert.Equal("prod", artifact.EnvironmentName);
        Assert.True(artifact.RetentionExpiresAt > new DateTime(2025, 6, 1, 12, 0, 0));
        // No project to resolve overrides for, and no environment cohort to age out.
        await _repo.DidNotReceive().GetByEnvironmentAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyBuildRetentionAsync_UsesProjectOverride_OverGlobalDefault()
    {
        // S-FEAT-15: project sets a 7-day "latest" override; the new build expires 7 days out.
        var artifact = new PipelineArtifact { Id = 1, PipelineId = 1, ProjectId = 1 };
        _repo.GetByPipelineAndProjectAsync(1, 1, ArtifactRetentionPolicy.Build, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineArtifact>());
        _repo.GetProjectRetentionOverridesAsync(1, Arg.Any<CancellationToken>())
            .Returns(((int?)2, (int?)7));

        await _sut.ApplyBuildRetentionAsync(artifact, ct: TestContext.Current.CancellationToken);

        Assert.Equal(new DateTime(2025, 6, 8, 12, 0, 0), artifact.RetentionExpiresAt);
    }

    [Fact]
    public async Task ApplyBuildRetentionAsync_DoesNotExtendPreviouslyShortenedDeadline()
    {
        var artifact = new PipelineArtifact { Id = 2, PipelineId = 1, ProjectId = 1 };
        var previousDeadline = new DateTime(2025, 6, 1, 18, 0, 0, DateTimeKind.Utc);
        var previous = new PipelineArtifact
        {
            Id = 1,
            PipelineId = 1,
            ProjectId = 1,
            RetentionExpiresAt = previousDeadline
        };
        _repo.GetByPipelineAndProjectAsync(1, 1, ArtifactRetentionPolicy.Build, Arg.Any<CancellationToken>())
            .Returns([previous, artifact]);

        await _sut.ApplyBuildRetentionAsync(artifact, ct: TestContext.Current.CancellationToken);

        Assert.Equal(previousDeadline, previous.RetentionExpiresAt);
    }

    [Fact]
    public async Task ApplyBuildRetentionAsync_ShortensLongerPreviousDeadline()
    {
        var artifact = new PipelineArtifact { Id = 2, PipelineId = 1, ProjectId = 1 };
        var previous = new PipelineArtifact
        {
            Id = 1,
            PipelineId = 1,
            ProjectId = 1,
            RetentionExpiresAt = new DateTime(2025, 7, 1, 12, 0, 0, DateTimeKind.Utc)
        };
        _repo.GetByPipelineAndProjectAsync(1, 1, ArtifactRetentionPolicy.Build, Arg.Any<CancellationToken>())
            .Returns([previous, artifact]);

        await _sut.ApplyBuildRetentionAsync(artifact, ct: TestContext.Current.CancellationToken);

        Assert.Equal(new DateTime(2025, 6, 2, 12, 0, 0), previous.RetentionExpiresAt);
    }

    [Fact]
    public async Task ApplyReleaseRetentionAsync_UpgradesPolicy()
    {
        var artifact = new PipelineArtifact { Id = 1, PipelineId = 1, ProjectId = 1, RetentionPolicy = ArtifactRetentionPolicy.Deployed };
        _repo.GetReleasesAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineArtifact>());

        await _sut.ApplyReleaseRetentionAsync(artifact, 42, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ArtifactRetentionPolicy.Released, artifact.RetentionPolicy);
        await _repo.Received(1).LinkReleaseAsync(artifact, 42, Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
