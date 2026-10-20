// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data.Entities;
using NSubstitute;

namespace Aetheus.Back.Tests.Git;

/// <summary>The mirror a pipeline takes its workspace from is fetched on demand.</summary>
public sealed class ExternalMirrorRefresherTests
{
    private readonly IGitLightRepository _repositories = Substitute.For<IGitLightRepository>();
    private readonly IExternalRepoMirrorService _mirror = Substitute.For<IExternalRepoMirrorService>();

    private ExternalMirrorRefresher Build() => new(_repositories, _mirror);

    private static GitInternalRepo Repository(string slug, GitProviderType provider) => new()
    {
        Id = 5,
        ProjectId = 1,
        Slug = slug,
        GitConnectionId = 8,
        GitConnection = new GitConnection { Id = 8, ProjectId = 1, ProviderType = provider }
    };

    [Fact]
    public async Task AMirror_IsFetched_AndAFailedFetchSaysWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        var mirrorRepo = Repository("api-public", GitProviderType.GitHub);
        _repositories.FindBySlugAsync(1, "api-public", Arg.Any<CancellationToken>()).Returns(mirrorRepo);
        _mirror.SyncAsync(mirrorRepo.GitConnection!, mirrorRepo, Arg.Any<CancellationToken>()).Returns(GitMirrorStatus.Ready);

        Assert.Null(await Build().RefreshAsync(1, "api-public", ct));
        // The fetch updates the mirror's state (default branch, status, date): it is saved.
        await _repositories.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        mirrorRepo.GitConnection!.LastFetchError = "could not resolve host";
        _mirror.SyncAsync(mirrorRepo.GitConnection!, mirrorRepo, Arg.Any<CancellationToken>()).Returns(GitMirrorStatus.Error);

        Assert.Equal("could not resolve host", await Build().RefreshAsync(1, "api-public", ct));
    }

    [Fact]
    public async Task TheProjectsOwnRepository_OrAnUnknownSlug_HasNothingToFetch()
    {
        var ct = TestContext.Current.CancellationToken;
        _repositories.FindBySlugAsync(1, "api", Arg.Any<CancellationToken>()).Returns(Repository("api", GitProviderType.AetheusGit));
        _repositories.FindBySlugAsync(1, "unknown", Arg.Any<CancellationToken>()).Returns((GitInternalRepo?)null);

        Assert.Null(await Build().RefreshAsync(1, "api", ct));
        Assert.Null(await Build().RefreshAsync(1, "unknown", ct));

        await _mirror.DidNotReceive().SyncAsync(Arg.Any<GitConnection>(), Arg.Any<GitInternalRepo>(), Arg.Any<CancellationToken>());
    }
}
