// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using NSubstitute;

namespace Aetheus.Back.Tests.Git;

public sealed class GitBranchMovedAnnouncerTests
{
    private readonly IDomainEventDispatcher _dispatcher = Substitute.For<IDomainEventDispatcher>();

    [Fact]
    public async Task Announce_DispatchesTheMovedBranchOfTheRepository_NotAPush()
    {
        var repo = new GitInternalRepo { Id = 3, ProjectId = 7, Slug = "aetheus", Name = "Aetheus", DefaultBranch = "develop" };
        GitBranchMovedEvent? seen = null;
        await _dispatcher.DispatchAsync(
            Arg.Do<GitBranchMovedEvent>(moved => seen = moved), Arg.Any<CancellationToken>());

        await new GitBranchMovedAnnouncer(_dispatcher).AnnounceAsync(
            repo, "/srv/git/7/aetheus.git", "main", "old", "new", TestContext.Current.CancellationToken);

        Assert.NotNull(seen);
        Assert.Equal((7, 3, "aetheus", "Aetheus", "develop", "/srv/git/7/aetheus.git"),
            (seen.ProjectId, seen.GitRepoId, seen.Slug, seen.RepositoryName, seen.DefaultBranch, seen.DiskPath));
        var update = Assert.Single(seen.RefUpdates);
        Assert.Equal(new GitRefUpdate("refs/heads/main", "new", "old"), update);
        // The one thing that must never happen here: announcing the move as a push would trigger the
        // pipelines watching the branch (ADR-044).
        await _dispatcher.DidNotReceiveWithAnyArgs().DispatchAsync(default(GitPushProcessedEvent)!, TestContext.Current.CancellationToken);
        _dispatcher.DidNotReceiveWithAnyArgs().Publish(default(GitPushProcessedEvent)!);
    }

    [Fact]
    public async Task Announce_WithoutAPreviousTip_CarriesAnEmptyOldObjectId()
    {
        var repo = new GitInternalRepo { Id = 3, ProjectId = 7, Slug = "aetheus", Name = "Aetheus" };
        GitBranchMovedEvent? seen = null;
        await _dispatcher.DispatchAsync(
            Arg.Do<GitBranchMovedEvent>(moved => seen = moved), Arg.Any<CancellationToken>());

        await new GitBranchMovedAnnouncer(_dispatcher).AnnounceAsync(
            repo, "/srv/git/7/aetheus.git", "production", null, "new", TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, Assert.Single(seen!.RefUpdates).OldObjectId);
    }
}
