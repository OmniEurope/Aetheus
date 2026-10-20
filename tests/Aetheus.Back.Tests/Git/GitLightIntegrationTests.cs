// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Webhooks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class GitLightIntegrationTests
{
    private readonly IGitLightRepository _lightRepoMock = Substitute.For<IGitLightRepository>();
    private readonly IGitRepository _gitRepoMock = Substitute.For<IGitRepository>();
    private readonly IGitLightCliService _cliMock = Substitute.For<IGitLightCliService>();
    private readonly IAuditService _auditMock = Substitute.For<IAuditService>();
    private readonly IHttpContextAccessor _httpMock = Substitute.For<IHttpContextAccessor>();
    private readonly IHubContext<GitRealtimeHub> _hubMock = Substitute.For<IHubContext<GitRealtimeHub>>();
    private readonly GitLightService _service;

    public GitLightIntegrationTests()
    {
        var options = Options.Create(new GitLightOptions { RepositoriesPath = "/tmp/test-repos" });
        _auditMock.LogAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("localhost", 5301);
        _httpMock.HttpContext.Returns(httpContext);

        var hubClients = Substitute.For<IHubClients>();
        var hubProxy = Substitute.For<IClientProxy>();
        hubClients.Group(Arg.Any<string>()).Returns(hubProxy);
        _hubMock.Clients.Returns(hubClients);

        _service = new GitLightService(
            _lightRepoMock,
            _gitRepoMock,
            _cliMock,
            _auditMock,
            options,
            _httpMock,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            _hubMock,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            new GitBranchProtectionService(_lightRepoMock, _cliMock, _auditMock, options),
            Substitute.For<ILogger<GitLightService>>(),
            TimeProvider.System);
    }

    // ── Branch Protection ────────────────────────────────────────────

    [Fact]
    public async Task CreateBranchProtectionRuleAsync_CreatesAndReturnsDto()
    {
        _lightRepoMock.AddBranchProtectionRuleAsync(Arg.Any<BranchProtectionRule>(), TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask);

        var request = new CreateBranchProtectionRuleRequest
        {
            Pattern = "main",
            PreventDeletion = true,
            PreventForcePush = true,
            ProtectionLevel = BranchProtectionLevel.NotProtected
        };

        var result = await _service.CreateBranchProtectionRuleAsync(1, request, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("main", result.Pattern);
        Assert.True(result.PreventDeletion);
        Assert.True(result.PreventForcePush);
        Assert.Equal(BranchProtectionLevel.NotProtected, result.ProtectionLevel);
        await _auditMock.Received(1).LogAsync("Created", "BranchProtectionRule",
            Arg.Any<int?>(), Arg.Any<string?>(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeleteBranchAsync_ProtectedBranch_ThrowsInvalidOperation()
    {
        var entity = new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test", DefaultBranch = "main" };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _lightRepoMock.GetBranchProtectionRulesAsync(1, TestContext.Current.CancellationToken)
            .Returns([new BranchProtectionRule
            {
                Id = 1, GitInternalRepoId = 1, Pattern = "main",
                PreventDeletion = true, PreventForcePush = true
            }]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeleteBranchAsync(1, "main", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteBranchAsync_UnprotectedBranch_Succeeds()
    {
        var entity = new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test", DefaultBranch = "main" };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _lightRepoMock.GetBranchProtectionRulesAsync(1, TestContext.Current.CancellationToken)
            .Returns([new BranchProtectionRule
            {
                Id = 1, GitInternalRepoId = 1, Pattern = "main",
                PreventDeletion = true, PreventForcePush = true
            }]);
        _cliMock.DeleteBranchAsync(Arg.Any<string>(), "feature", TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask);

        await _service.DeleteBranchAsync(1, "feature", ct: TestContext.Current.CancellationToken);

        await _cliMock.Received(1).DeleteBranchAsync(Arg.Any<string>(), "feature", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeleteBranchAsync_WildcardProtection_BlocksMatchingBranches()
    {
        var entity = new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test", DefaultBranch = "main" };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _lightRepoMock.GetBranchProtectionRulesAsync(1, TestContext.Current.CancellationToken)
            .Returns([new BranchProtectionRule
            {
                Id = 1, GitInternalRepoId = 1, Pattern = "release/*",
                PreventDeletion = true, PreventForcePush = true
            }]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeleteBranchAsync(1, "release/v1.0", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsBranchProtectedAsync_ExactMatch_ReturnsTrue()
    {
        _lightRepoMock.GetBranchProtectionRulesAsync(1, TestContext.Current.CancellationToken)
            .Returns([new BranchProtectionRule { Pattern = "main", PreventDeletion = true }]);

        var result = await _service.IsBranchProtectedAsync(1, "main", ct: TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task IsBranchProtectedAsync_NoMatch_ReturnsFalse()
    {
        _lightRepoMock.GetBranchProtectionRulesAsync(1, TestContext.Current.CancellationToken)
            .Returns([new BranchProtectionRule { Pattern = "main", PreventDeletion = true }]);

        var result = await _service.IsBranchProtectedAsync(1, "develop", ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    // ── Commit Graph ─────────────────────────────────────────────────

    [Fact]
    public async Task GetCommitGraphAsync_ReturnsGraphOutput()
    {
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken)
            .Returns(new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test" });
        _cliMock.GetCommitGraphAsync(Arg.Any<string>(), 100, TestContext.Current.CancellationToken)
            .Returns("* abc1234 Initial commit");

        var result = await _service.GetCommitGraphAsync(1, 100, ct: TestContext.Current.CancellationToken);

        Assert.Contains("abc1234", result);
    }

    [Fact]
    public async Task GetCommitGraphAsync_RepoNotFound_ReturnsEmpty()
    {
        _lightRepoMock.FindByIdAsync(99, TestContext.Current.CancellationToken)
            .Returns((GitInternalRepo?)null);

        var result = await _service.GetCommitGraphAsync(99, 100, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetCommitGraphAsync_ClampsMaxCount()
    {
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken)
            .Returns(new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test" });
        _cliMock.GetCommitGraphAsync(Arg.Any<string>(), 500, TestContext.Current.CancellationToken)
            .Returns("* commit");

        var result = await _service.GetCommitGraphAsync(1, 9999, ct: TestContext.Current.CancellationToken);

        await _cliMock.Received(1).GetCommitGraphAsync(Arg.Any<string>(), 500, TestContext.Current.CancellationToken);
    }

    // ── Smart HTTP Webhook ───────────────────────────────────────────

    [Fact]
    public async Task MarkPushedAsync_FiresWebhook()
    {
        var lightRepo = Substitute.For<IGitLightRepository>();
        var authService = Substitute.For<Back.Components.Auth.IAuthService>();
        var webhookService = Substitute.For<IWebhookService>();
        var options = Options.Create(new GitLightOptions { RepositoriesPath = "/tmp/repos" });

        var entity = new GitInternalRepo
        {
            Id = 1,
            ProjectId = 1,
            Name = "Test",
            Slug = "test",
            IsEmpty = true
        };
        lightRepo.FindBySlugAsync(1, "test", Arg.Any<CancellationToken>()).Returns(entity);
        lightRepo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        webhookService.FireEventAsync("git.push", Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var smartHubMock = Substitute.For<IHubContext<GitRealtimeHub>>();
        var smartHubClients = Substitute.For<IHubClients>();
        var smartHubProxy = Substitute.For<IClientProxy>();
        smartHubClients.Group(Arg.Any<string>()).Returns(smartHubProxy);
        smartHubMock.Clients.Returns(smartHubClients);

        var smartHttp = new GitSmartHttpService(
            lightRepo,
            authService,
            webhookService,
            Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>(),
            Substitute.For<IGitLightCliService>(),
            smartHubMock,
            options,
            Substitute.For<ILogger<GitSmartHttpService>>(),
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            TimeProvider.System,
            Substitute.For<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());

        await smartHttp.MarkPushedAsync(1, "test", [], ct: TestContext.Current.CancellationToken);

        Assert.False(entity.IsEmpty);
        Assert.NotNull(entity.LastPushAt);
    }

    // ── README Detection (via tree) ──────────────────────────────────

    [Fact]
    public async Task GetTreeAsync_ReturnsEntries()
    {
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken)
            .Returns(new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test", DefaultBranch = "main" });
        _cliMock.GetTreeAsync(Arg.Any<string>(), Arg.Is("main"), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([
                new GitLightTreeEntryDto { Name = "README.md", Path = "README.md", Type = GitTreeEntryType.Blob, Size = 500 },
                new GitLightTreeEntryDto { Name = "src", Path = "src", Type = GitTreeEntryType.Tree }
            ]);

        var result = await _service.GetTreeAsync(1, null, null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        var readme = result.First(e => e.Name == "README.md");
        Assert.Equal(GitTreeEntryType.Blob, readme.Type);
    }

    [Fact]
    public async Task GetBlobAsync_ReturnsContent()
    {
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken)
            .Returns(new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test" });
        _cliMock.GetBlobAsync(Arg.Any<string>(), "main", "README.md", TestContext.Current.CancellationToken)
            .Returns(new GitLightBlobDto { Path = "README.md", Content = "# Hello", Size = 7, IsBinary = false });

        var result = await _service.GetBlobAsync(1, "main", "README.md", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("# Hello", result.Content);
        Assert.False(result.IsBinary);
    }

    // ── Branch Protection Update/Delete ──────────────────────────────

    [Fact]
    public async Task UpdateBranchProtectionRuleAsync_NotFound_ReturnsNull()
    {
        _lightRepoMock.FindBranchProtectionRuleAsync(99, TestContext.Current.CancellationToken)
            .Returns((BranchProtectionRule?)null);

        var result = await _service.UpdateBranchProtectionRuleAsync(1, 99,
            new UpdateBranchProtectionRuleRequest(), ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateBranchProtectionRuleAsync_Found_UpdatesAndReturns()
    {
        var rule = new BranchProtectionRule
        {
            Id = 1,
            GitInternalRepoId = 1,
            Pattern = "main",
            PreventDeletion = true,
            PreventForcePush = false
        };
        _lightRepoMock.FindBranchProtectionRuleAsync(1, TestContext.Current.CancellationToken).Returns(rule);
        _lightRepoMock.SaveChangesAsync(TestContext.Current.CancellationToken).Returns(Task.CompletedTask);

        var result = await _service.UpdateBranchProtectionRuleAsync(1, 1,
            new UpdateBranchProtectionRuleRequest
            {
                PreventDeletion = false,
                PreventForcePush = true,
                ProtectionLevel = BranchProtectionLevel.StrictlyProtected
            }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result.PreventDeletion);
        Assert.True(result.PreventForcePush);
        Assert.Equal(BranchProtectionLevel.StrictlyProtected, result.ProtectionLevel);
        // D13 expand step: the column the previous colour enforces follows the level.
        Assert.True(rule.RequirePullRequest);
    }

    [Fact]
    public async Task DeleteBranchProtectionRuleAsync_NotFound_ReturnsFalse()
    {
        _lightRepoMock.FindBranchProtectionRuleAsync(99, TestContext.Current.CancellationToken)
            .Returns((BranchProtectionRule?)null);

        var result = await _service.DeleteBranchProtectionRuleAsync(1, 99, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task DeleteBranchProtectionRuleAsync_Found_DeletesAndReturnsTrue()
    {
        var rule = new BranchProtectionRule { Id = 1, GitInternalRepoId = 1, Pattern = "main" };
        _lightRepoMock.FindBranchProtectionRuleAsync(1, TestContext.Current.CancellationToken).Returns(rule);
        _lightRepoMock.RemoveBranchProtectionRuleAsync(rule, TestContext.Current.CancellationToken).Returns(Task.CompletedTask);

        var result = await _service.DeleteBranchProtectionRuleAsync(1, 1, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _lightRepoMock.Received(1).RemoveBranchProtectionRuleAsync(rule, TestContext.Current.CancellationToken);
    }

    // ── Delegating read methods (repo-missing guard + CLI passthrough) ──

    [Fact]
    public async Task GetBlameAsync_RepoMissing_ReturnsEmpty()
    {
        _lightRepoMock.FindByIdAsync(99, TestContext.Current.CancellationToken).Returns((GitInternalRepo?)null);

        Assert.Empty(await _service.GetBlameAsync(99, "main", "README.md", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetBlameAsync_RepoFound_DelegatesToCli()
    {
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test" });
        _cliMock.GetBlameAsync(Arg.Any<string>(), "main", "README.md", Arg.Any<CancellationToken>())
            .Returns([new GitLightBlameLine()]);

        var blame = await _service.GetBlameAsync(1, "main", "README.md", ct: TestContext.Current.CancellationToken);

        Assert.Single(blame);
    }

    [Fact]
    public async Task GetCommitGraphDataAsync_RepoMissing_ReturnsEmpty()
    {
        _lightRepoMock.FindByIdAsync(99, TestContext.Current.CancellationToken).Returns((GitInternalRepo?)null);

        Assert.Empty(await _service.GetCommitGraphDataAsync(99, 50, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetCommitGraphDataAsync_RepoFound_DelegatesToCli()
    {
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test" });
        _cliMock.GetCommitsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([new GitLightCommitDto { Sha = "abc", ShortSha = "abc", Message = "init" }]);

        var commits = await _service.GetCommitGraphDataAsync(1, 50, ct: TestContext.Current.CancellationToken);

        Assert.Single(commits);
    }

    [Fact]
    public async Task GetBlobStreamAsync_RepoMissing_ReturnsNull()
    {
        _lightRepoMock.FindByIdAsync(99, TestContext.Current.CancellationToken).Returns((GitInternalRepo?)null);

        Assert.Null(await _service.GetBlobStreamAsync(99, "main", "README.md", ct: TestContext.Current.CancellationToken));
    }
}
