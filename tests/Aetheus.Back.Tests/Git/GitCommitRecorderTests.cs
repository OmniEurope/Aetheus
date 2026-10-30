// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>A commit written by Aetheus clears the repository's empty flag.</summary>
public class GitCommitRecorderTests
{
    private readonly IHubContext<GitRealtimeHub> _hub = Substitute.For<IHubContext<GitRealtimeHub>>();
    private readonly IClientProxy _group = Substitute.For<IClientProxy>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 11, 28, 0, TimeSpan.Zero));

    public GitCommitRecorderTests()
    {
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(_group);
        _hub.Clients.Returns(clients);
    }

    private readonly IGitDevelopBranchProvisioner _developBranches = Substitute.For<IGitDevelopBranchProvisioner>();

    private GitCommitRecorder Sut(IGitLightRepository repository) => new(
        repository, _hub, new MemoryCache(new MemoryCacheOptions()), _time, _developBranches,
        NullLogger<GitCommitRecorder>.Instance);

    [Fact]
    public async Task RecordCommitAsync_EmptyRepository_IsNoLongerEmptyAndStamped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"git-commit-recorder-{Guid.NewGuid():N}").Options);
        db.GitInternalRepos.Add(new GitInternalRepo { Id = 7, ProjectId = 5, Name = "ats", Slug = "ats", IsEmpty = true });
        await db.SaveChangesAsync(ct);

        await Sut(new GitLightRepository(db)).RecordCommitAsync(7, ct);

        var stored = await db.GitInternalRepos.AsNoTracking().SingleAsync(repository => repository.Id == 7, ct);
        Assert.False(stored.IsEmpty);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, stored.LastPushAt);
        await _group.Received(1).SendCoreAsync(
            GitRealtimeEvents.RepositoryChanged, Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    /// <summary>R-76: only the commit that fills an empty repository asks for the develop branch.</summary>
    [Fact]
    public async Task RecordCommitAsync_TheFirstCommit_AsksForTheDevelopBranch_AndALaterOneDoesNot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"git-commit-recorder-{Guid.NewGuid():N}").Options);
        db.GitInternalRepos.Add(new GitInternalRepo { Id = 7, ProjectId = 5, Name = "ats", Slug = "ats", IsEmpty = true });
        await db.SaveChangesAsync(ct);
        var sut = Sut(new GitLightRepository(db));

        await sut.RecordCommitAsync(7, ct);
        await sut.RecordCommitAsync(7, ct);

        await _developBranches.Received(1).EnsureAfterFirstCommitAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordCommitAsync_UnknownRepository_BroadcastsNothing()
    {
        var repository = Substitute.For<IGitLightRepository>();
        repository.MarkCommittedAsync(99, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns((int?)null);

        await Sut(repository).RecordCommitAsync(99, TestContext.Current.CancellationToken);

        await _group.DidNotReceiveWithAnyArgs().SendCoreAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RecordCommitAsync_BroadcastFails_TheRowIsStillMarked()
    {
        var repository = Substitute.For<IGitLightRepository>();
        repository.MarkCommittedAsync(3, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(1);
        _group.SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("hub down")));

        await Sut(repository).RecordCommitAsync(3, TestContext.Current.CancellationToken);

        await repository.Received(1).MarkCommittedAsync(3, _time.GetUtcNow().UtcDateTime, Arg.Any<CancellationToken>());
    }
}
