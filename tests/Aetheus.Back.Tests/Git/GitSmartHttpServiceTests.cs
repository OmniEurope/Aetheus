// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Components.Webhooks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class GitSmartHttpServiceTests : IDisposable
{
    private readonly IGitLightRepository _lightRepoMock = Substitute.For<IGitLightRepository>();
    private readonly IAuthService _authServiceMock = Substitute.For<IAuthService>();
    private readonly IWebhookService _webhookMock = Substitute.For<IWebhookService>();
    // Git only announces the push now; the dispatcher is what this module owes.
    private readonly IDomainEventDispatcher _domainEventsMock = Substitute.For<IDomainEventDispatcher>();
    private readonly IGitLightCliService _cliMock = Substitute.For<IGitLightCliService>();
    private readonly TimeProvider _timeProvider = Substitute.For<TimeProvider>();
    private readonly IHostApplicationLifetime _applicationLifetime = Substitute.For<IHostApplicationLifetime>();
    private readonly CancellationTokenSource _applicationStopping = new();
    private readonly GitSmartHttpService _sut;

    public GitSmartHttpServiceTests()
    {
        var hubClients = Substitute.For<IHubClients>();
        var clientProxy = Substitute.For<IClientProxy>();
        hubClients.Group(Arg.Any<string>()).Returns(clientProxy);
        var hub = Substitute.For<IHubContext<GitRealtimeHub>>();
        hub.Clients.Returns(hubClients);

        var options = Options.Create(new GitLightOptions { RepositoriesPath = "/tmp/repos" });
        _timeProvider.GetUtcNow().Returns(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        _applicationLifetime.ApplicationStopping.Returns(_applicationStopping.Token);

        _sut = new GitSmartHttpService(
            _lightRepoMock,
            _authServiceMock,
            _webhookMock,
            _domainEventsMock,
            _cliMock,
            hub,
            options,
            NullLogger<GitSmartHttpService>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            _timeProvider,
            _applicationLifetime);
    }

    public void Dispose() => _applicationStopping.Dispose();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateServiceProcessStartInfo_UploadPack_AllowsOnlyReachablePinnedCommits(bool advertiseRefs)
    {
        var result = GitSmartHttpService.CreateServiceProcessStartInfo(
            "git-upload-pack", "/srv/git/repo.git", advertiseRefs);

        var arguments = result.ArgumentList.ToArray();
        Assert.Equal("git", result.FileName);
        Assert.Contains("uploadpack.allowReachableSHA1InWant=true", arguments);
        Assert.DoesNotContain(arguments, argument =>
            argument.Contains("allowAnySHA1InWant", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(advertiseRefs, arguments.Contains("--advertise-refs", StringComparer.Ordinal));
        Assert.Equal(!advertiseRefs, result.RedirectStandardInput);
        Assert.Equal("/srv/git/repo.git", arguments[^1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateServiceProcessStartInfo_ReceivePack_DoesNotRelaxUploadRules(bool advertiseRefs)
    {
        var result = GitSmartHttpService.CreateServiceProcessStartInfo(
            "git-receive-pack", "/srv/git/repo.git", advertiseRefs);

        Assert.DoesNotContain(result.ArgumentList, argument =>
            argument.Contains("allowReachableSHA1InWant", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("receive-pack", result.ArgumentList[0]);
    }

    // --- GetInfoRefsAsync ---

    [Theory]
    [InlineData("evil-service")]
    [InlineData("git-evil")]
    [InlineData("")]
    public async Task GetInfoRefsAsync_DisallowedService_ReturnsNull(string service)
    {
        var result = await _sut.GetInfoRefsAsync(1, "repo", service, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetInfoRefsAsync_RepoNotFound_ReturnsNull()
    {
        _lightRepoMock.FindBySlugAsync(1, "missing", Arg.Any<CancellationToken>())
            .Returns((GitInternalRepo?)null);

        var result = await _sut.GetInfoRefsAsync(1, "missing", "git-upload-pack", ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("git-upload-pack")]
    [InlineData("git-receive-pack")]
    public async Task GetInfoRefsAsync_AllowedService_PassesServiceGuardAndLooksUpRepo(string service)
    {
        _lightRepoMock.FindBySlugAsync(1, "my-repo", Arg.Any<CancellationToken>())
            .Returns(new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "my-repo" });

        // The actual git process output depends on git being installed (returns null in CI when the
        // process start fails), so this test deliberately verifies only the deterministic part: an
        // allowed service passes the service allow-list guard and the repo lookup is performed.
        _ = await _sut.GetInfoRefsAsync(1, "my-repo", service, ct: TestContext.Current.CancellationToken);

        await _lightRepoMock.Received(1).FindBySlugAsync(1, "my-repo", Arg.Any<CancellationToken>());
    }

    // --- ValidateBasicAuthAsync (delegates to IAuthService) ---

    [Fact]
    public async Task ValidateBasicAuthAsync_DelegatesToAuthService_ReturnsFalse()
    {
        _authServiceMock.ValidateBasicAuthAsync("unknown", "password", Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.ValidateBasicAuthAsync("unknown", "password", ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        await _authServiceMock.Received(1).ValidateBasicAuthAsync("unknown", "password", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateBasicAuthAsync_DelegatesToAuthService_ReturnsTrue()
    {
        _authServiceMock.ValidateBasicAuthAsync("admin", "correct", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.ValidateBasicAuthAsync("admin", "correct", ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _authServiceMock.Received(1).ValidateBasicAuthAsync("admin", "correct", Arg.Any<CancellationToken>());
    }

    // --- MarkPushedAsync ---

    [Fact]
    public async Task MarkPushedAsync_RepoNotFound_DoesNothing()
    {
        _lightRepoMock.FindBySlugAsync(1, "missing", Arg.Any<CancellationToken>())
            .Returns((GitInternalRepo?)null);

        await _sut.MarkPushedAsync(1, "missing", [], ct: TestContext.Current.CancellationToken);

        await _lightRepoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkPushedAsync_ValidRepo_SetsIsEmptyFalseAndTimestamps()
    {
        var repo = new GitInternalRepo
        {
            Id = 1,
            ProjectId = 1,
            Slug = "my-repo",
            Name = "My Repo",
            IsEmpty = true,
            DefaultBranch = "main"
        };
        _lightRepoMock.FindBySlugAsync(1, "my-repo", Arg.Any<CancellationToken>()).Returns(repo);
        _cliMock.GetTreeAsync(Arg.Any<string>(), "HEAD", ".pipeline", Arg.Any<CancellationToken>())
            .Returns(new List<Aetheus.Shared.Components.Git.GitLightTreeEntryDto>());

        await _sut.MarkPushedAsync(1, "my-repo", [], ct: TestContext.Current.CancellationToken);

        Assert.False(repo.IsEmpty);
        Assert.NotEqual(default, repo.LastPushAt);
        await _lightRepoMock.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies push announcements carrying the accepted commit and repository path under
    /// the application-lifetime token. GitPushPipelineTriggerHandlerTests covers subscriber
    /// behavior (ADR-039).</summary>
    [Fact]
    public async Task MarkPushedAsync_AnnouncesTheAcceptedPushOnTheLifetimeToken()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        var repo = new GitInternalRepo
        {
            Id = 1,
            ProjectId = 7,
            Slug = "repo",
            Name = "Repo",
            DefaultBranch = "main"
        };
        _lightRepoMock.FindBySlugAsync(7, "repo", Arg.Any<CancellationToken>()).Returns(repo);

        using var disconnectedClient = new CancellationTokenSource();
        disconnectedClient.Cancel();

        await _sut.MarkPushedAsync(
            7, "repo", [new GitRefUpdate("refs/heads/main", commit)], disconnectedClient.Token);

        await _lightRepoMock.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(token => token == _applicationLifetime.ApplicationStopping));
        await _webhookMock.Received(1).FireEventAsync(
            "git.push", Arg.Any<object>(),
            Arg.Is<CancellationToken>(token => token == _applicationLifetime.ApplicationStopping));
        // A360-21: PUBLISHED, not dispatched. The announcement is observer semantics and the subscriber
        // now runs a full pipeline launch, preflight included; awaiting it here made `git push` wait for
        // the orchestrator. Publish takes no token by design - it hands the work to the background
        // queue, which already runs on the application lifetime rather than the client's connection.
        _domainEventsMock.Received(1).Publish(
            Arg.Is<GitPushProcessedEvent>(push =>
                push.ProjectId == 7
                && push.GitRepoId == repo.Id
                && push.DefaultBranch == "main"
                && push.RefUpdates.Count == 1
                && push.RefUpdates[0].NewObjectId == commit));
        await _domainEventsMock.DidNotReceive().DispatchAsync(
            Arg.Any<GitPushProcessedEvent>(), Arg.Any<CancellationToken>());
    }
}
