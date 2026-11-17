// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.ExternalRepos;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Components.ServiceConnections;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.ExternalRepos;

/// <summary>
/// Recette R-4 (decision of 2026-11-10, option B): a project that names a public external address gets a project mirror
/// created from that address, through the existing attach path (connectivity probe, mirror clone, audit), and from then
/// on names the mirror's clone URL; the address itself is only the mirror's remote. The repositories, the probe and the
/// clone are doubles: this proves the coordinates handed to the mirror and the project's new source, not a real clone.
/// </summary>
public sealed class ExternalRepoMirrorAddressTests
{
    private readonly IProjectRepository _projects = Substitute.For<IProjectRepository>();
    private readonly IGitRepository _git = Substitute.For<IGitRepository>();
    private readonly IGitLightRepository _light = Substitute.For<IGitLightRepository>();
    private readonly IServiceConnectionRepository _connections = Substitute.For<IServiceConnectionRepository>();
    private readonly IExternalRepoMirrorService _mirror = Substitute.For<IExternalRepoMirrorService>();

    public ExternalRepoMirrorAddressTests()
    {
        _light.GetByProjectAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _mirror.TestConnectivityAsync(
                Arg.Any<GitProviderType>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<GitAuthType>(), Arg.Any<GitCredentialPayload?>(), Arg.Any<CancellationToken>())
            .Returns((true, (string?)null));
        _mirror.SyncAsync(Arg.Any<GitConnection>(), Arg.Any<GitInternalRepo>(), Arg.Any<CancellationToken>())
            .Returns(GitMirrorStatus.Ready);
        _git.AddConnectionAsync(Arg.Any<GitConnection>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<GitConnection>().Id = 31;
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task MirrorRepositoryAddress_PublicAddress_CreatesTheMirrorAndThenNamesItsCloneUrl()
    {
        var project = new Project { Id = 1, Name = "OE", RepositoryUrl = "https://github.com/omnieurope/OmniEurope.Blazor.git", DefaultBranch = "develop" };
        _projects.FindProjectAsync(1, Arg.Any<CancellationToken>()).Returns(project);

        var dto = await Build().MirrorRepositoryAddressAsync(1, TestContext.Current.CancellationToken);

        await _mirror.Received(1).TestConnectivityAsync(
            GitProviderType.GitHub, null, "omnieurope", "OmniEurope.Blazor", GitAuthType.HttpsToken,
            Arg.Is<GitCredentialPayload?>(credential => credential != null && credential.Token == null),
            Arg.Any<CancellationToken>());
        await _mirror.Received(1).SyncAsync(
            Arg.Is<GitConnection>(connection => !connection.WriteEnabled && connection.AutoSyncEnabled),
            Arg.Any<GitInternalRepo>(), Arg.Any<CancellationToken>());
        Assert.True(dto.IsProjectSource);
        Assert.Equal(31, project.GitConnectionId);
        Assert.Equal("https://api.example/git/1/omnieurope-blazor.git", project.RepositoryUrl);
        Assert.False(ExternalRepositoryAddress.IsUnmirrored(project.RepositoryUrl));
    }

    [Fact]
    public async Task MirrorRepositoryAddress_UnreachableAddress_IsRefusedAndTheProjectKeepsItsAddress()
    {
        const string url = "https://git.example.org/team/app.git";
        var project = new Project { Id = 1, RepositoryUrl = url };
        _projects.FindProjectAsync(1, Arg.Any<CancellationToken>()).Returns(project);
        _mirror.TestConnectivityAsync(
                Arg.Any<GitProviderType>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<GitAuthType>(), Arg.Any<GitCredentialPayload?>(), Arg.Any<CancellationToken>())
            .Returns((false, "The host 'git.example.org' resolves to a private or reserved address."));

        var refusal = await Assert.ThrowsAsync<BadRequestException>(
            () => Build().MirrorRepositoryAddressAsync(1, TestContext.Current.CancellationToken));

        Assert.Contains("private or reserved", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(url, project.RepositoryUrl);
        Assert.Null(project.GitConnectionId);
        await _mirror.Received(1).TestConnectivityAsync(
            GitProviderType.Gitea, "https://git.example.org", "team", "app", GitAuthType.HttpsToken,
            Arg.Any<GitCredentialPayload?>(), Arg.Any<CancellationToken>());
        await _mirror.DidNotReceiveWithAnyArgs().SyncAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("https://api.example/git/1/app.git", null)]
    [InlineData("https://github.com/team/app.git", 8)]
    [InlineData(null, null)]
    public async Task MirrorRepositoryAddress_NothingLeftToMirror_IsAConflict(string? url, int? connectionId)
    {
        _projects.FindProjectAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Project { Id = 1, RepositoryUrl = url, GitConnectionId = connectionId });

        await Assert.ThrowsAsync<ConflictException>(() => Build().MirrorRepositoryAddressAsync(1, TestContext.Current.CancellationToken));
        await AssertNothingContactedAsync();
    }

    [Fact]
    public async Task MirrorRepositoryAddress_BesideARepositoryOfTheProject_IsAConflict()
    {
        var project = new Project { Id = 1, RepositoryUrl = "https://github.com/team/app.git" };
        _projects.FindProjectAsync(1, Arg.Any<CancellationToken>()).Returns(project);
        _light.GetByProjectAsync(1, Arg.Any<CancellationToken>())
            .Returns([new GitInternalRepo { Id = 4, ProjectId = 1, Slug = "app" }]);

        await Assert.ThrowsAsync<ConflictException>(() => Build().MirrorRepositoryAddressAsync(1, TestContext.Current.CancellationToken));
        await AssertNothingContactedAsync();
    }

    [Theory]
    [InlineData("http://github.com/team/app.git")]
    [InlineData("https://user:token@github.com/team/app.git")]
    [InlineData("https://github.com:8443/team/app.git")]
    [InlineData("https://github.com/team/app.git?ref=main")]
    [InlineData("https://github.com/app.git")]
    [InlineData("https://10.0.0.5/team/app.git")]
    [InlineData("git@github.com:team/app.git")]
    [InlineData("https://github.com/team/../app.git")]
    public async Task MirrorRepositoryAddress_NotAPublicHttpsRepositoryAddress_IsRefusedBeforeAnyContact(string url)
    {
        _projects.FindProjectAsync(1, Arg.Any<CancellationToken>()).Returns(new Project { Id = 1, RepositoryUrl = url });

        await Assert.ThrowsAsync<BadRequestException>(() => Build().MirrorRepositoryAddressAsync(1, TestContext.Current.CancellationToken));
        await AssertNothingContactedAsync();
    }

    [Theory]
    [InlineData("https://github.com/team/app.git", GitProviderType.GitHub, null, "team", "app")]
    [InlineData("https://gitlab.com/group/sub/app", GitProviderType.GitLab, null, "group/sub", "app")]
    [InlineData("https://gitlab.acme.org/group/app.git", GitProviderType.GitLab, "https://gitlab.acme.org", "group", "app")]
    [InlineData("https://Code.Example.org/team/app.git", GitProviderType.Gitea, "https://code.example.org", "team", "app")]
    public void Parse_PublicAddress_GivesTheCoordinatesTheMirrorRebuildsTheSameAddressFrom(
        string url, GitProviderType provider, string? baseUrl, string owner, string repository)
    {
        var address = PublicRepositoryAddress.Parse(url);

        Assert.Equal(new PublicRepositoryAddress(provider, baseUrl, owner, repository), address);
        Assert.Equal(
            url.ToLowerInvariant().EndsWith(".git", StringComparison.Ordinal) ? url.ToLowerInvariant() : $"{url.ToLowerInvariant()}.git",
            ExternalRepoUrlBuilder.Build(address.Provider, address.BaseUrl, address.OwnerOrGroup, address.RepositoryName, GitAuthType.HttpsToken)
                .ToLowerInvariant());
    }

    private async Task AssertNothingContactedAsync()
    {
        await _mirror.DidNotReceiveWithAnyArgs().TestConnectivityAsync(default, default, default!, default!, default, default, TestContext.Current.CancellationToken);
        await _mirror.DidNotReceiveWithAnyArgs().SyncAsync(default!, default!, TestContext.Current.CancellationToken);
        await _git.DidNotReceiveWithAnyArgs().AddConnectionAsync(default!, TestContext.Current.CancellationToken);
    }

    private ExternalRepoService Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Aetheus:PublicApiBaseUrl"] = "https://api.example" })
            .Build();
        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(call => $"enc:{call.Arg<string>()}");
        return new ExternalRepoService(
            _projects, _git, _light, _connections, _mirror, encryption, Substitute.For<IAuditService>(),
            Substitute.For<IEntityChangeNotifier>(), Substitute.For<IHttpContextAccessor>(), config,
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero)),
            NullLogger<ExternalRepoService>.Instance);
    }
}
