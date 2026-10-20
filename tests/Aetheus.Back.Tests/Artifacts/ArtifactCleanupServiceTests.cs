// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Artifacts;

public class ArtifactCleanupServiceTests
{
    private static (ArtifactCleanupService Sut, IArtifactRepository Repo, IArtifactStorageService Storage, IPostgresLeaderLease OperationLock, DateTime Now)
        BuildSut()
    {
        var repo = Substitute.For<IArtifactRepository>();
        var storage = Substitute.For<IArtifactStorageService>();
        var operationLock = Substitute.For<IPostgresLeaderLease>();
        operationLock.RunSerializedAsync(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.ArgAt<CancellationToken>(2)));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));

        var services = new ServiceCollection();
        services.AddScoped(_ => repo);
        services.AddScoped(_ => storage);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var sut = new ArtifactCleanupService(
            scopeFactory, NullLogger<ArtifactCleanupService>.Instance, clock, operationLock,
            Substitute.For<IChunkedArtifactUploadService>());
        return (sut, repo, storage, operationLock, clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task CleanupExpiredArtifacts_RemovesTheRowBeforeDeletingTheFile()
    {
        var (sut, repo, storage, operationLock, now) = BuildSut();
        var artifact = new PipelineArtifact { Id = 2, FilePath = "test/good.zip" };
        repo.GetExpiredAsync(now, 100, Arg.Any<CancellationToken>()).Returns([artifact]);
        repo.RemoveAsync(artifact, Arg.Any<CancellationToken>()).Returns(true);

        await sut.CleanupExpiredArtifactsAsync(TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            repo.RemoveAsync(artifact, Arg.Any<CancellationToken>());
            storage.DeleteArtifactAsync("test/good.zip", Arg.Any<CancellationToken>());
        });
        await operationLock.Received(1).RunSerializedAsync(
            ArtifactRetentionLock.For(artifact.Id),
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupExpiredArtifacts_RowDeleteRefused_KeepsTheFileAndCleansTheNextArtifact()
    {
        // The defect: a finished Dependency-Track delivery's RESTRICT foreign key refused the row delete
        // AFTER the file was gone, and the exception ended the whole pass. Now the refused artifact keeps
        // both its row and its file, and the artifacts behind it are still cleaned.
        var (sut, repo, storage, _, now) = BuildSut();
        var refused = new PipelineArtifact { Id = 4, FilePath = "test/sbom.zip" };
        var next = new PipelineArtifact { Id = 5, FilePath = "test/next.zip" };
        repo.GetExpiredAsync(now, 100, Arg.Any<CancellationToken>()).Returns([refused, next]);
        repo.RemoveAsync(refused, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("23503 foreign key violation")));
        repo.RemoveAsync(next, Arg.Any<CancellationToken>()).Returns(true);

        await sut.CleanupExpiredArtifactsAsync(TestContext.Current.CancellationToken);

        await storage.DidNotReceive().DeleteArtifactAsync("test/sbom.zip", Arg.Any<CancellationToken>());
        await repo.Received(1).RemoveAsync(next, Arg.Any<CancellationToken>());
        await storage.Received(1).DeleteArtifactAsync("test/next.zip", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupExpiredArtifacts_FileDeleteFailsAfterTheRowIsGone_LeavesItToTheUnreferencedSweep()
    {
        var (sut, repo, storage, _, now) = BuildSut();
        var artifact = new PipelineArtifact { Id = 1, FilePath = "test/locked.zip" };
        var next = new PipelineArtifact { Id = 6, FilePath = "test/after.zip" };
        repo.GetExpiredAsync(now, 100, Arg.Any<CancellationToken>()).Returns([artifact, next]);
        repo.RemoveAsync(Arg.Any<PipelineArtifact>(), Arg.Any<CancellationToken>()).Returns(true);
        storage.DeleteArtifactAsync("test/locked.zip", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("file locked")));

        await sut.CleanupExpiredArtifactsAsync(TestContext.Current.CancellationToken);

        await repo.Received(1).RemoveAsync(artifact, Arg.Any<CancellationToken>());
        await storage.Received(1).DeleteArtifactAsync("test/after.zip", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupExpiredArtifacts_NoExpired_DoesNothing()
    {
        var (sut, repo, storage, _, now) = BuildSut();
        repo.GetExpiredAsync(now, 100, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineArtifact>());

        await sut.CleanupExpiredArtifactsAsync(TestContext.Current.CancellationToken);

        await storage.DidNotReceive().DeleteArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().RemoveAsync(Arg.Any<PipelineArtifact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupExpiredArtifacts_LeaseAcquiredAfterSelection_SkipsPhysicalDelete()
    {
        var (sut, repo, storage, _, now) = BuildSut();
        var artifact = new PipelineArtifact { Id = 3, FilePath = "test/raced.zip" };
        repo.GetExpiredAsync(now, 100, Arg.Any<CancellationToken>()).Returns([artifact]);
        repo.HasActiveRetentionLeaseAsync(artifact.Id, now, Arg.Any<CancellationToken>()).Returns(true);

        await sut.CleanupExpiredArtifactsAsync(TestContext.Current.CancellationToken);

        await storage.DidNotReceive().DeleteArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().RemoveAsync(Arg.Any<PipelineArtifact>(), Arg.Any<CancellationToken>());
    }
}
