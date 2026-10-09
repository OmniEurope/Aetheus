// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Artifacts;

public class ArtifactServiceTests
{
    private readonly IArtifactRepository _repoMock = Substitute.For<IArtifactRepository>();
    private readonly IArtifactStorageService _storageMock = Substitute.For<IArtifactStorageService>();
    private readonly IArtifactRetentionService _retentionMock = Substitute.For<IArtifactRetentionService>();
    // The cross-module reads are now own-reads on the artifact repository, so the stubs move with them.
    private readonly IDbTransactionScope _transactionMock = Substitute.For<IDbTransactionScope>();
    private readonly ArtifactService _sut;

    public ArtifactServiceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ArtifactStorage:ProjectQuotaBytes"] = "1000" })
            .Build();
        _sut = new ArtifactService(
            _repoMock,
            _storageMock,
            _retentionMock,
            config,
            NullLogger<ArtifactService>.Instance,
            _transactionMock);
    }

    // --- PublishArtifactAsync ---

    [Fact]
    public async Task PublishArtifactAsync_RunNotFound_ReturnsNull()
    {
        _repoMock.GetRunPipelineContextAsync(99, Arg.Any<CancellationToken>()).Returns(((int PipelineId, int? ProjectId)?)null);

        var result = await _sut.PublishArtifactAsync(99, "build", null, 0, Stream.Null, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task PublishArtifactAsync_ValidRun_SavesAndReturnsDto()
    {
        _repoMock.GetRunPipelineContextAsync(1, Arg.Any<CancellationToken>())
            .Returns((PipelineId: 10, ProjectId: (int?)5));
        _storageMock.SaveArtifactAsync(5, 10, 1, "build-1.zip", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(("artifacts/5/10/1/build-1.zip", "abc123checksum"));
        _storageMock.GetArtifactSize("artifacts/5/10/1/build-1.zip").Returns(1024L);

        var result = await _sut.PublishArtifactAsync(1, "build", "compile", 0, Stream.Null, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("build", result.Name);
        Assert.Equal(1024, result.SizeBytes);
        Assert.Equal("abc123checksum", result.Sha256);
        Assert.Equal("compile", result.StageName);
        Assert.Equal(1, result.PipelineRunId);
        await _repoMock.Received(1).AddAsync(Arg.Any<PipelineArtifact>(), Arg.Any<CancellationToken>());
        await _retentionMock.Received(1).ApplyBuildRetentionAsync(Arg.Any<PipelineArtifact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishArtifactAsync_QuotaReached_ThrowsConflict_AndDoesNotSave()
    {
        _repoMock.GetRunPipelineContextAsync(1, Arg.Any<CancellationToken>())
            .Returns((PipelineId: 10, ProjectId: (int?)5));
        // Test config sets the project quota to 1000 bytes; report the project already at 1500.
        _repoMock.GetProjectTotalSizeBytesAsync(5, Arg.Any<CancellationToken>()).Returns(1500L);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.PublishArtifactAsync(1, "build", null, 100, Stream.Null, ct: TestContext.Current.CancellationToken));

        await _storageMock.DidNotReceive().SaveArtifactAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().AddAsync(Arg.Any<PipelineArtifact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishArtifactAsync_QuotaPressure_EvictsOldBuildButRetainsNewestPerPipeline()
    {
        _repoMock.GetRunPipelineContextAsync(1, Arg.Any<CancellationToken>())
            .Returns((PipelineId: 10, ProjectId: (int?)5));
        _repoMock.GetProjectTotalSizeBytesAsync(5, Arg.Any<CancellationToken>()).Returns(1500L);
        var oldest = new PipelineArtifact
        {
            Id = 1,
            PipelineId = 10,
            ProjectId = 5,
            FilePath = "old.zip",
            SizeBytes = 600,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var newest = new PipelineArtifact
        {
            Id = 2,
            PipelineId = 10,
            ProjectId = 5,
            FilePath = "new.zip",
            SizeBytes = 900,
            CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        };
        _repoMock.GetProjectBuildArtifactsAsync(5, Arg.Any<CancellationToken>())
            .Returns([oldest, newest]);
        _storageMock.SaveArtifactAsync(5, 10, 1, "build-1.zip", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(("current.zip", "sha"));

        var result = await _sut.PublishArtifactAsync(1, "build", null, 100, Stream.Null, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _storageMock.Received(1).DeleteArtifactAsync("old.zip", Arg.Any<CancellationToken>());
        await _repoMock.Received(1).RemoveAsync(oldest, Arg.Any<CancellationToken>());
        await _storageMock.DidNotReceive().DeleteArtifactAsync("new.zip", Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().RemoveAsync(newest, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishArtifactAsync_QuotaPressure_NeverEvictsAnArtifactUnderRetentionLease()
    {
        _repoMock.GetRunPipelineContextAsync(1, Arg.Any<CancellationToken>())
            .Returns((PipelineId: 10, ProjectId: (int?)5));
        _repoMock.GetProjectTotalSizeBytesAsync(5, Arg.Any<CancellationToken>()).Returns(1500L);
        var leased = new PipelineArtifact
        {
            Id = 1,
            PipelineId = 10,
            ProjectId = 5,
            FilePath = "leased.zip",
            SizeBytes = 600,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            RetentionLeaseExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        var newest = new PipelineArtifact
        {
            Id = 2,
            PipelineId = 10,
            ProjectId = 5,
            FilePath = "new.zip",
            SizeBytes = 900,
            CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        };
        _repoMock.GetProjectBuildArtifactsAsync(5, Arg.Any<CancellationToken>()).Returns([leased, newest]);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.PublishArtifactAsync(1, "build", null, 100, Stream.Null, ct: TestContext.Current.CancellationToken));

        await _storageMock.DidNotReceive().DeleteArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().RemoveAsync(Arg.Any<PipelineArtifact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishArtifactAsync_QuotaPressure_KeepsAnArtifactLeasedAfterTheCandidateQuery()
    {
        _repoMock.GetRunPipelineContextAsync(1, Arg.Any<CancellationToken>())
            .Returns((PipelineId: 10, ProjectId: (int?)5));
        _repoMock.GetProjectTotalSizeBytesAsync(5, Arg.Any<CancellationToken>()).Returns(1500L);
        var oldest = new PipelineArtifact
        {
            Id = 1,
            PipelineId = 10,
            ProjectId = 5,
            FilePath = "old.zip",
            SizeBytes = 600,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var newest = new PipelineArtifact
        {
            Id = 2,
            PipelineId = 10,
            ProjectId = 5,
            FilePath = "new.zip",
            SizeBytes = 900,
            CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        };
        _repoMock.GetProjectBuildArtifactsAsync(5, Arg.Any<CancellationToken>()).Returns([oldest, newest]);
        // A checkpoint resume took the lease between the candidate query and the eviction.
        _repoMock.HasActiveRetentionLeaseAsync(1, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.PublishArtifactAsync(1, "build", null, 100, Stream.Null, ct: TestContext.Current.CancellationToken));

        await _storageMock.DidNotReceive().DeleteArtifactAsync("old.zip", Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().RemoveAsync(oldest, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishArtifactAsync_NoOwningProject_RefusedBeforeAnyByteOrRowIsWritten()
    {
        // BACK-009: this used to store the file and the row, then fail in retention with a null
        // ProjectId, leaving an orphan behind a 500.
        _repoMock.GetRunPipelineContextAsync(1, Arg.Any<CancellationToken>())
            .Returns((PipelineId: 10, ProjectId: (int?)null));

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.PublishArtifactAsync(1, "build", null, 0, Stream.Null, ct: TestContext.Current.CancellationToken));

        await _storageMock.DidNotReceiveWithAnyArgs().SaveArtifactAsync(default, default, default, default!, default!, TestContext.Current.CancellationToken);
        await _repoMock.DidNotReceive().AddAsync(Arg.Any<PipelineArtifact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishArtifactAsync_LinksReleaseCreatedBeforePostStageCollection()
    {
        _repoMock.GetRunPipelineContextAsync(42, Arg.Any<CancellationToken>())
            .Returns((PipelineId: 10, ProjectId: (int?)5));
        _storageMock.SaveArtifactAsync(5, 10, 42, "build-42.zip", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(("artifacts/5/10/42/build-42.zip", "checksum"));
        _storageMock.GetArtifactSize("artifacts/5/10/42/build-42.zip").Returns(1024L);
        _repoMock.FindReleaseForRunAsync(42, Arg.Any<CancellationToken>())
            .Returns(new Release { Id = 8, PipelineRunId = 42 });

        await _sut.PublishArtifactAsync(42, "build", "package", 0, Stream.Null, ct: TestContext.Current.CancellationToken);

        await _retentionMock.Received(1).ApplyReleaseRetentionAsync(
            Arg.Is<PipelineArtifact>(artifact => artifact.PipelineRunId == 42), 8, Arg.Any<CancellationToken>());
    }

    // --- MarkDeployedAsync (deploy-success closure) ---

    [Fact]
    public async Task MarkDeployedAsync_MissingArtifact_ReturnsFalse()
    {
        _repoMock.FindAsync(99, Arg.Any<CancellationToken>()).Returns((PipelineArtifact?)null);

        var ok = await _sut.MarkDeployedAsync(99, "toto", ct: TestContext.Current.CancellationToken);

        Assert.False(ok);
        await _retentionMock.DidNotReceive().ApplyDeployRetentionAsync(Arg.Any<PipelineArtifact>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkDeployedAsync_AppliesDeployRetention()
    {
        var artifact = new PipelineArtifact { Id = 7, ProjectId = 3, PipelineId = 10, PipelineRunId = 5 };
        _repoMock.FindAsync(7, Arg.Any<CancellationToken>()).Returns(artifact);

        var ok = await _sut.MarkDeployedAsync(7, "toto", ct: TestContext.Current.CancellationToken);

        Assert.True(ok);
        await _retentionMock.Received(1).ApplyDeployRetentionAsync(artifact, "toto", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkDeployedAsync_FlipsLinkedReleasesToDeployed()
    {
        var release = new Release { Id = 1, Status = ReleaseStatus.Published };
        var otherLinkedRelease = new Release { Id = 9, Status = ReleaseStatus.Published };
        var artifact = new PipelineArtifact { Id = 7, ProjectId = 3, Releases = { release, otherLinkedRelease } };
        _repoMock.FindAsync(7, Arg.Any<CancellationToken>()).Returns(artifact);
        var previous = new Release { Id = 2, ProjectId = 3, Status = ReleaseStatus.Deployed };
        _repoMock.GetDeployedProjectReleasesAsync(3, Arg.Any<CancellationToken>()).Returns([previous]);
        _transactionMock.IsRelational.Returns(true);

        await _sut.MarkDeployedAsync(7, "toto", releaseId: 1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseStatus.Deployed, release.Status);
        Assert.Equal(ReleaseStatus.Published, otherLinkedRelease.Status);
        Assert.Equal(ReleaseStatus.Superseded, previous.Status);
        await _repoMock.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkDeployedAsync_ReleaseNotLinkedToArtifact_RollsBack()
    {
        var artifact = new PipelineArtifact
        {
            Id = 7,
            ProjectId = 3,
            Releases = { new Release { Id = 1, Status = ReleaseStatus.Published } }
        };
        _repoMock.FindAsync(7, Arg.Any<CancellationToken>()).Returns(artifact);
        _transactionMock.IsRelational.Returns(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.MarkDeployedAsync(7, "toto", releaseId: 9, ct: TestContext.Current.CancellationToken));

        await _transactionMock.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
        await _transactionMock.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkDeployedAsync_NoReleases_DoesNotSaveForFlip()
    {
        var artifact = new PipelineArtifact { Id = 7, ProjectId = 3 }; // no releases (same-run artifact)
        _repoMock.FindAsync(7, Arg.Any<CancellationToken>()).Returns(artifact);

        var ok = await _sut.MarkDeployedAsync(7, "toto", ct: TestContext.Current.CancellationToken);

        Assert.True(ok);
        // retention saves on its own; the release-flip path must not trigger an extra SaveChanges here.
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // --- GetArtifactAsync ---

    [Fact]
    public async Task GetArtifactAsync_NotFound_ReturnsNull()
    {
        _repoMock.FindAsync(99, Arg.Any<CancellationToken>()).Returns((PipelineArtifact?)null);

        var result = await _sut.GetArtifactAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetArtifactAsync_Found_ReturnsMappedDto()
    {
        var artifact = new PipelineArtifact
        {
            Id = 1,
            Name = "output.zip",
            FilePath = "/data/output.zip",
            SizeBytes = 2048,
            PipelineRunId = 5,
            PipelineId = 10,
            Pipeline = new Pipeline { Id = 10, SourceRepositoryId = 23 },
            ProjectId = 3,
            StageName = "build",
            RetentionPolicy = ArtifactRetentionPolicy.Build
        };
        _repoMock.FindAsync(1, Arg.Any<CancellationToken>()).Returns(artifact);

        var result = await _sut.GetArtifactAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(1, result.Id);
        Assert.Equal("output.zip", result.Name);
        Assert.Equal(2048, result.SizeBytes);
        Assert.Equal(ArtifactRetentionPolicy.Build, result.RetentionPolicy);
        Assert.Equal(23, result.SourceRepositoryId);
    }

    // --- DownloadArtifactAsync ---

    [Fact]
    public async Task DownloadArtifactAsync_NotFound_ReturnsNull()
    {
        _repoMock.FindAsync(99, Arg.Any<CancellationToken>()).Returns((PipelineArtifact?)null);

        var result = await _sut.DownloadArtifactAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadArtifactAsync_Found_OpensAndReturnsStream()
    {
        var artifact = new PipelineArtifact { Id = 1, FilePath = "/data/file.zip" };
        _repoMock.FindAsync(1, Arg.Any<CancellationToken>()).Returns(artifact);
        var ms = new MemoryStream([1, 2, 3]);
        _storageMock.OpenArtifact("/data/file.zip").Returns(ms);

        var result = await _sut.DownloadArtifactAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Same(ms, result);
    }

    // --- PromoteToEnvironmentAsync ---

    [Fact]
    public async Task PromoteToEnvironmentAsync_NotFound_ReturnsNull()
    {
        _repoMock.FindAsync(99, Arg.Any<CancellationToken>()).Returns((PipelineArtifact?)null);

        var result = await _sut.PromoteToEnvironmentAsync(99, "staging", ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task PromoteToEnvironmentAsync_Found_AppliesRetentionAndReturnsDto()
    {
        var artifact = new PipelineArtifact { Id = 1, Name = "build.zip", PipelineRunId = 1, PipelineId = 1 };
        _repoMock.FindAsync(1, Arg.Any<CancellationToken>()).Returns(artifact);

        var result = await _sut.PromoteToEnvironmentAsync(1, "production", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _retentionMock.Received(1).ApplyDeployRetentionAsync(artifact, "production", Arg.Any<CancellationToken>());
    }

    // --- PromoteToReleaseAsync ---

    [Fact]
    public async Task PromoteToReleaseAsync_NotFound_ReturnsNull()
    {
        _repoMock.FindAsync(99, Arg.Any<CancellationToken>()).Returns((PipelineArtifact?)null);

        var result = await _sut.PromoteToReleaseAsync(99, 1, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task PromoteToReleaseAsync_Found_AppliesRetentionAndReturnsDto()
    {
        var artifact = new PipelineArtifact { Id = 1, Name = "build.zip", PipelineRunId = 1, PipelineId = 1 };
        _repoMock.FindAsync(1, Arg.Any<CancellationToken>()).Returns(artifact);
        _repoMock.GetArtifactOwningProjectIdAsync(1, Arg.Any<CancellationToken>()).Returns(7);
        _repoMock.GetReleaseProjectIdAsync(42, Arg.Any<CancellationToken>()).Returns(7);

        var result = await _sut.PromoteToReleaseAsync(1, 42, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _retentionMock.Received(1).ApplyReleaseRetentionAsync(artifact, 42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteToReleaseAsync_ReleaseOfAnotherProject_RefusesAndAppliesNoRetention()
    {
        var artifact = new PipelineArtifact { Id = 1, Name = "build.zip", PipelineRunId = 1, PipelineId = 1 };
        _repoMock.FindAsync(1, Arg.Any<CancellationToken>()).Returns(artifact);
        _repoMock.GetArtifactOwningProjectIdAsync(1, Arg.Any<CancellationToken>()).Returns(7);
        _repoMock.GetReleaseProjectIdAsync(42, Arg.Any<CancellationToken>()).Returns(9);

        var result = await _sut.PromoteToReleaseAsync(1, 42, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
        await _retentionMock.DidNotReceive().ApplyReleaseRetentionAsync(
            Arg.Any<PipelineArtifact>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteToReleaseAsync_UnresolvableOwner_RefusesRatherThanPromoting()
    {
        var artifact = new PipelineArtifact { Id = 1, Name = "build.zip", PipelineRunId = 1, PipelineId = 1 };
        _repoMock.FindAsync(1, Arg.Any<CancellationToken>()).Returns(artifact);
        // A legacy pipeline with no owner at all: no project can be named, so no comparison can be
        // made, so the promotion must not happen.
        _repoMock.GetArtifactOwningProjectIdAsync(1, Arg.Any<CancellationToken>()).Returns((int?)null);
        _repoMock.GetReleaseProjectIdAsync(42, Arg.Any<CancellationToken>()).Returns(7);

        Assert.Null(await _sut.PromoteToReleaseAsync(1, 42, ct: TestContext.Current.CancellationToken));
        await _retentionMock.DidNotReceive().ApplyReleaseRetentionAsync(
            Arg.Any<PipelineArtifact>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // --- GetProjectArtifactsAsync ---

    [Fact]
    public async Task GetProjectArtifactsAsync_ReturnsPaginatedResult()
    {
        var artifacts = new List<PipelineArtifact>
        {
            new() { Id = 1, Name = "a.zip", PipelineRunId = 1, PipelineId = 1, SizeBytes = 100 },
            new() { Id = 2, Name = "b.zip", PipelineRunId = 2, PipelineId = 1, SizeBytes = 200 }
        };
        _repoMock.GetByProjectPagedAsync(5, null, null, 1, 25, Arg.Any<CancellationToken>())
            .Returns((artifacts, 2));

        var result = await _sut.GetProjectArtifactsAsync(5, new ProjectArtifactsRequest(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("a.zip", result.Items[0].Name);
        Assert.Equal("b.zip", result.Items[1].Name);
    }

    [Fact]
    public async Task GetProjectArtifactsAsync_WithPolicyFilter_PassesToRepo()
    {
        _repoMock.GetByProjectPagedAsync(5, ArtifactRetentionPolicy.Deployed, null, 1, 25, Arg.Any<CancellationToken>())
            .Returns((new List<PipelineArtifact>(), 0));

        var result = await _sut.GetProjectArtifactsAsync(5, new ProjectArtifactsRequest { Policy = ArtifactRetentionPolicy.Deployed }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.TotalCount);
        await _repoMock.Received(1).GetByProjectPagedAsync(5, ArtifactRetentionPolicy.Deployed, null, 1, 25, Arg.Any<CancellationToken>());
    }

    // --- IsAgentAssignedToRunAsync ---

    [Fact]
    public async Task IsAgentAssignedToRunAsync_DelegatesToPipelineRepo()
    {
        _repoMock.IsServerAssignedToRunAsync(1, 2, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.IsAgentAssignedToRunAsync(1, 2, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repoMock.Received(1).IsServerAssignedToRunAsync(1, 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsAgentAssignedToRunAsync_NotAssigned_ReturnsFalse()
    {
        _repoMock.IsServerAssignedToRunAsync(1, 2, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.IsAgentAssignedToRunAsync(1, 2, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    // --- OpenArtifactForAgentAsync (IDOR guard: the cross-agent deploy download boundary) ---

    private PipelineArtifact ArrangeAgentArtifact(int orgId = 7)
        => new()
        {
            Id = 100,
            Name = "release",
            FilePath = "artifacts/5/10/1/release.zip",
            ProjectId = 5,
            PipelineRunId = 88, // build run - deliberately NOT the deploy run
            Project = new Project { Id = 5, Name = "p", OrganizationId = orgId }
        };

    [Fact]
    public async Task OpenArtifactForAgentAsync_AgentNotAssignedToDeployRun_ReturnsForbidden()
    {
        _repoMock.FindAsync(100, Arg.Any<CancellationToken>()).Returns(ArrangeAgentArtifact());
        // Agent (server 2) is NOT a participant of the deploy run 42 it claims to execute.
        _repoMock.IsServerAssignedToRunAsync(42, 2, Arg.Any<CancellationToken>()).Returns(false);

        var (status, stream, _) = await _sut.OpenArtifactForAgentAsync(100, 42, 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(DeployDownloadStatus.Forbidden, status);
        Assert.Null(stream);
        _storageMock.DidNotReceive().OpenArtifact(Arg.Any<string>());
    }

    [Fact]
    public async Task OpenArtifactForAgentAsync_CrossOrgArtifact_ReturnsForbidden()
    {
        _repoMock.FindAsync(100, Arg.Any<CancellationToken>()).Returns(ArrangeAgentArtifact(orgId: 7));
        _repoMock.IsServerAssignedToRunAsync(42, 2, Arg.Any<CancellationToken>()).Returns(true);
        // Agent's server belongs to a DIFFERENT org (9) than the artifact's project org (7).
        _repoMock.GetServerOrganizationIdAsync(2, Arg.Any<CancellationToken>()).Returns(9);

        var (status, stream, _) = await _sut.OpenArtifactForAgentAsync(100, 42, 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(DeployDownloadStatus.Forbidden, status);
        Assert.Null(stream);
        _storageMock.DidNotReceive().OpenArtifact(Arg.Any<string>());
    }

    [Fact]
    public async Task OpenArtifactForAgentAsync_ProjectlessArtifact_ReturnsForbidden()
    {
        _repoMock.FindAsync(100, Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifact { Id = 100, Name = "orphan", FilePath = "p", ProjectId = null });

        var (status, _, _) = await _sut.OpenArtifactForAgentAsync(100, 42, 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(DeployDownloadStatus.Forbidden, status);
    }

    [Fact]
    public async Task OpenArtifactForAgentAsync_AssignedSameOrg_ReturnsOk()
    {
        _repoMock.FindAsync(100, Arg.Any<CancellationToken>()).Returns(ArrangeAgentArtifact(orgId: 7));
        _repoMock.IsServerAssignedToRunAsync(42, 2, Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetServerOrganizationIdAsync(2, Arg.Any<CancellationToken>()).Returns(7);
        _storageMock.OpenArtifact("artifacts/5/10/1/release.zip").Returns(new MemoryStream([1, 2, 3]));

        var (status, stream, fileName) = await _sut.OpenArtifactForAgentAsync(100, 42, 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(DeployDownloadStatus.Ok, status);
        Assert.NotNull(stream);
        Assert.Equal("release.zip", fileName);
    }

    [Fact]
    public async Task OpenArtifactForAgentAsync_ArtifactNotFound_ReturnsNotFound()
    {
        _repoMock.FindAsync(100, Arg.Any<CancellationToken>()).Returns((PipelineArtifact?)null);

        var (status, _, _) = await _sut.OpenArtifactForAgentAsync(100, 42, 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(DeployDownloadStatus.NotFound, status);
    }
}
