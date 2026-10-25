// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class GitLightServiceTests
{
    private readonly IGitLightRepository _lightRepoMock = Substitute.For<IGitLightRepository>();
    private readonly IGitRepository _gitRepoMock = Substitute.For<IGitRepository>();
    private readonly IGitLightCliService _cliMock = Substitute.For<IGitLightCliService>();
    private readonly IAuditService _auditMock = Substitute.For<IAuditService>();
    private readonly IHttpContextAccessor _httpMock = Substitute.For<IHttpContextAccessor>();
    private readonly IHubContext<GitRealtimeHub> _hubMock = Substitute.For<IHubContext<GitRealtimeHub>>();
    private readonly GitLightService _sut;

    public GitLightServiceTests()
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

        _sut = new GitLightService(
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
            Substitute.For<IGitBranchMovedAnnouncer>(),
            Substitute.For<ILogger<GitLightService>>(),
            TimeProvider.System);
    }

    [Fact]
    public async Task GetRepositoriesAsync_ReturnsMappedDtos()
    {
        _lightRepoMock.GetByProjectAsync(1, TestContext.Current.CancellationToken)
            .Returns([new GitInternalRepo
            {
                Id = 1, ProjectId = 1, Name = "Test", Slug = "test",
                DefaultBranch = "main", IsEmpty = true,
                Project = new Project { Id = 1, Name = "MyProject" }
            }]);

        var result = await _sut.GetRepositoriesAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("Test", result[0].Name);
        Assert.Equal("test", result[0].Slug);
        Assert.Equal("MyProject", result[0].ProjectName);
    }

    [Fact]
    public async Task GetRepositoryAsync_NotFound_ReturnsNull()
    {
        _lightRepoMock.FindByIdWithProjectAsync(99, TestContext.Current.CancellationToken)
            .Returns((GitInternalRepo?)null);

        var result = await _sut.GetRepositoryAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetRepositoryAsync_Found_ReturnsDto()
    {
        _lightRepoMock.FindByIdWithProjectAsync(1, TestContext.Current.CancellationToken)
            .Returns(new GitInternalRepo
            {
                Id = 1,
                ProjectId = 1,
                Name = "Repo",
                Slug = "repo",
                DefaultBranch = "main",
                Project = new Project { Id = 1, Name = "P1" }
            });

        var result = await _sut.GetRepositoryAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Repo", result.Name);
        Assert.Contains("repo.git", result.CloneUrl);
    }

    [Fact]
    public async Task GetCommitMessagesAsync_UsesSingleBatchCliCall()
    {
        var shas = new[] { "abcdef12", "12345678" };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken)
            .Returns(CreateRepoEntity());
        _cliMock.GetCommitMessagesAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), TestContext.Current.CancellationToken)
            .Returns(new Dictionary<string, string> { [shas[0]] = "feat: one", [shas[1]] = "fix: two" });

        var result = await _sut.GetCommitMessagesAsync(1, shas, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        await _cliMock.Received(1).GetCommitMessagesAsync(
            Arg.Any<string>(), Arg.Is<IReadOnlyCollection<string>>(values => values.Count == 2),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetCommitDetailAsync_UppercaseRootSha_NormalizesAndUsesFormatIndependentRootPatch()
    {
        const string routeSha = "ABCDEF12";
        const string normalizedSha = "abcdef12";
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken)
            .Returns(new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "repo" });
        _cliMock.GetCommitsAsync(
                Arg.Any<string>(),
                normalizedSha,
                0,
                1,
                null,
                null,
                TestContext.Current.CancellationToken)
            .Returns(
            [
                new GitLightCommitDto
                {
                    Sha = normalizedSha,
                    ShortSha = normalizedSha,
                    ParentShas = []
                }
            ]);
        _cliMock.GetRootCommitPatchAsync(
                Arg.Any<string>(),
                normalizedSha,
                TestContext.Current.CancellationToken)
            .Returns(new GitPatchResult(string.Empty, false));

        var result = await _sut.GetCommitDetailAsync(
            1,
            routeSha,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _cliMock.Received(1).GetRootCommitPatchAsync(
            Arg.Any<string>(),
            normalizedSha,
            TestContext.Current.CancellationToken);
        await _cliMock.DidNotReceiveWithAnyArgs().GetCommitPatchAsync(
            default!,
            default!,
            default!,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateRepositoryAsync_InitsBareRepo_And_CreatesConnection()
    {
        var request = new CreateGitLightRepoRequest
        {
            ProjectId = 1,
            Name = "My Repo",
            Description = "Test description"
        };

        _lightRepoMock.ProjectExistsAsync(1, TestContext.Current.CancellationToken).Returns(true);
        _cliMock.InitBareRepoAsync(Arg.Any<string>(), "main", TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask);
        _lightRepoMock.AddAsync(Arg.Any<GitInternalRepo>(), TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask);
        _gitRepoMock.AddConnectionAsync(Arg.Any<GitConnection>(), TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask);
        _lightRepoMock.SaveChangesAsync(TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask);
        _lightRepoMock.FindByIdWithProjectAsync(Arg.Any<int>(), TestContext.Current.CancellationToken)
            .Returns(new GitInternalRepo
            {
                Id = 1,
                ProjectId = 1,
                Name = "My Repo",
                Slug = "my-repo",
                DefaultBranch = "main",
                Project = new Project { Id = 1, Name = "P1" }
            });

        var result = await _sut.CreateRepositoryAsync(request, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("my-repo", result.Slug);
        await _cliMock.Received(1).InitBareRepoAsync(Arg.Any<string>(), "main", TestContext.Current.CancellationToken);
        await _gitRepoMock.Received(1).AddConnectionAsync(
            Arg.Is<GitConnection>(c => c.ProviderType == GitProviderType.AetheusGit), TestContext.Current.CancellationToken);
        await _auditMock.Received(1).LogAsync("Created", "GitInternalRepo", Arg.Any<int?>(), Arg.Any<string?>(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateRepositoryAsync_ProjectNotFound_ThrowsNotFound_AndTouchesNothing()
    {
        // ProjectExistsAsync defaults to false (unmapped) - the FK guard must fire before
        // the disk is touched, so a bad ProjectId never leaves an orphan bare repo behind
        // and never reaches the raw 23503 foreign-key violation.
        var request = new CreateGitLightRepoRequest { ProjectId = 999, Name = "Orphan Repo" };

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.CreateRepositoryAsync(request, ct: TestContext.Current.CancellationToken));

        await _cliMock.DidNotReceive().InitBareRepoAsync(Arg.Any<string>(), Arg.Any<string>(), TestContext.Current.CancellationToken);
        await _lightRepoMock.DidNotReceive().AddAsync(Arg.Any<GitInternalRepo>(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureRepositoryInitializedAsync_ReinitializesTrackedRepositoryIdempotently()
    {
        _lightRepoMock.FindByIdAsync(7, TestContext.Current.CancellationToken)
            .Returns(new GitInternalRepo
            {
                Id = 7,
                ProjectId = 3,
                Slug = "demo",
                DefaultBranch = "main"
            });

        await _sut.EnsureRepositoryInitializedAsync(7, TestContext.Current.CancellationToken);

        await _cliMock.Received(1).InitBareRepoAsync(
            Arg.Is<string>(path =>
                path.EndsWith(Path.Combine("3", "demo.git"), StringComparison.Ordinal)
                && path.Contains("test-repos", StringComparison.Ordinal)),
            "main",
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeleteRepositoryAsync_NotFound_ReturnsFalse()
    {
        _lightRepoMock.FindByIdAsync(99, TestContext.Current.CancellationToken)
            .Returns((GitInternalRepo?)null);

        var result = await _sut.DeleteRepositoryAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task DeleteRepositoryAsync_Found_DeletesRepoAndConnection()
    {
        var entity = new GitInternalRepo
        {
            Id = 1,
            ProjectId = 1,
            Slug = "test",
            GitConnectionId = 10
        };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _cliMock.DeleteRepoAsync(Arg.Any<string>(), TestContext.Current.CancellationToken).Returns(Task.CompletedTask);
        _gitRepoMock.FindConnectionAsync(10, TestContext.Current.CancellationToken)
            .Returns(new GitConnection { Id = 10 });
        _gitRepoMock.RemoveConnectionAsync(Arg.Any<GitConnection>(), TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask);
        _lightRepoMock.RemoveAsync(entity, TestContext.Current.CancellationToken).Returns(Task.CompletedTask);

        var result = await _sut.DeleteRepositoryAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _cliMock.Received(1).DeleteRepoAsync(Arg.Any<string>(), TestContext.Current.CancellationToken);
        await _gitRepoMock.Received(1).RemoveConnectionAsync(Arg.Any<GitConnection>(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetCommitsAsync_RepoNotFound_ReturnsEmpty()
    {
        _lightRepoMock.FindByIdAsync(99, TestContext.Current.CancellationToken)
            .Returns((GitInternalRepo?)null);

        var result = await _sut.GetCommitsAsync(99, null, 1, 30, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task GetCommitsAsync_AllBranchesSentinel_WalksEveryRef()
    {
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken)
            .Returns(new GitInternalRepo
            {
                Id = 1,
                ProjectId = 1,
                Slug = "test",
                DefaultBranch = "main"
            });
        _cliMock.GetCommitsAsync(
                Arg.Any<string>(),
                Arg.Is<string?>(value => value == null),
                Arg.Is(0),
                Arg.Is(30),
                Arg.Is<string?>(value => value == null),
                Arg.Is<string?>(value => value == null),
                Arg.Is(TestContext.Current.CancellationToken))
            .Returns([new GitLightCommitDto { Sha = "abcdef12", ShortSha = "abcdef1" }]);
        _cliMock.GetCommitCountAsync(
                Arg.Any<string>(),
                Arg.Is<string?>(value => value == null),
                Arg.Is<string?>(value => value == null),
                Arg.Is(TestContext.Current.CancellationToken))
            .Returns(1);

        var result = await _sut.GetCommitsAsync(
            1,
            GitReference.AllBranches,
            1,
            30,
            ct: TestContext.Current.CancellationToken);

        Assert.Single(result.Items);
        Assert.Equal(1, result.TotalCount);
        await _cliMock.Received(1).GetCommitsAsync(
            Arg.Any<string>(),
            Arg.Is<string?>(value => value == null),
            Arg.Is(0),
            Arg.Is(30),
            Arg.Is<string?>(value => value == null),
            Arg.Is<string?>(value => value == null),
            Arg.Is(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetBranchesAsync_ReturnsCliResult()
    {
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken)
            .Returns(new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test", DefaultBranch = "main" });
        _cliMock.GetBranchesAsync(Arg.Any<string>(), "main", TestContext.Current.CancellationToken)
            .Returns([new GitLightBranchDto { Name = "main", IsDefault = true }]);

        var result = await _sut.GetBranchesAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("main", result[0].Name);
    }

    [Fact]
    public async Task UpdateRepositoryAsync_NotFound_ReturnsNull()
    {
        _lightRepoMock.FindByIdAsync(99, TestContext.Current.CancellationToken)
            .Returns((GitInternalRepo?)null);

        var result = await _sut.UpdateRepositoryAsync(99, new UpdateGitLightRepoRequest { Description = "x" }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateRepositoryAsync_DefaultBranch_RepointsHeadAndPersistsSelection()
    {
        var entity = new GitInternalRepo
        {
            Id = 1,
            ProjectId = 1,
            Name = "Repo",
            Slug = "repo",
            DefaultBranch = "develop",
            IsEmpty = false,
            Project = new Project { Id = 1, Name = "Project" }
        };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _lightRepoMock.FindByIdWithProjectAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _cliMock.GetBranchesAsync(Arg.Any<string>(), "develop", TestContext.Current.CancellationToken).Returns(
        [
            new GitLightBranchDto { Name = "develop", IsDefault = true },
            new GitLightBranchDto { Name = "main" }
        ]);

        var result = await _sut.UpdateRepositoryAsync(1, new UpdateGitLightRepoRequest
        {
            DefaultBranch = "main"
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("main", result.DefaultBranch);
        Assert.Equal("main", entity.DefaultBranch);
        await _cliMock.Received(1).SetHeadAsync(Arg.Any<string>(), "main", TestContext.Current.CancellationToken);
        await _lightRepoMock.Received(1).SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UpdateRepositoryAsync_UnknownDefaultBranch_IsRejected()
    {
        var entity = new GitInternalRepo
        {
            Id = 1,
            ProjectId = 1,
            Slug = "repo",
            DefaultBranch = "develop",
            IsEmpty = false
        };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _cliMock.GetBranchesAsync(Arg.Any<string>(), "develop", TestContext.Current.CancellationToken).Returns(
            [new GitLightBranchDto { Name = "develop", IsDefault = true }]);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.UpdateRepositoryAsync(1, new UpdateGitLightRepoRequest { DefaultBranch = "missing" }, ct: TestContext.Current.CancellationToken));

        await _cliMock.DidNotReceive().SetHeadAsync(Arg.Any<string>(), Arg.Any<string>(), TestContext.Current.CancellationToken);
        await _lightRepoMock.DidNotReceive().SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UpdateRepositoryAsync_DefaultBranchCase_IsNormalizedToRepositoryBranch()
    {
        var entity = new GitInternalRepo
        {
            Id = 1,
            ProjectId = 1,
            Slug = "repo",
            DefaultBranch = "develop",
            IsEmpty = false,
            Project = new Project { Id = 1, Name = "Project" }
        };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _lightRepoMock.FindByIdWithProjectAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _cliMock.GetBranchesAsync(Arg.Any<string>(), "develop", TestContext.Current.CancellationToken).Returns(
            [new GitLightBranchDto { Name = "Main" }]);

        var result = await _sut.UpdateRepositoryAsync(
            1, new UpdateGitLightRepoRequest { DefaultBranch = "main" },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Main", result!.DefaultBranch);
        Assert.Equal("Main", entity.DefaultBranch);
        await _cliMock.Received(1).SetHeadAsync(
            Arg.Any<string>(), "Main", TestContext.Current.CancellationToken);
    }

    private static GitInternalRepo ProjectRepo(int id, bool configuration, bool mirror = false) => new()
    {
        Id = id,
        ProjectId = 1,
        Name = $"repo-{id}",
        Slug = $"repo-{id}",
        DefaultBranch = "main",
        IsConfiguration = configuration,
        Project = new Project { Id = 1, Name = "Project" },
        GitConnection = new GitConnection
        {
            Id = 50 + id,
            ProviderType = mirror ? GitProviderType.GitHub : GitProviderType.AetheusGit
        }
    };

    private void ProjectHolds(params GitInternalRepo[] repositories)
    {
        _lightRepoMock.GetByProjectAsync(1, TestContext.Current.CancellationToken).Returns([.. repositories]);
        foreach (var repository in repositories)
        {
            _lightRepoMock.FindByIdAsync(repository.Id, TestContext.Current.CancellationToken).Returns(repository);
            _lightRepoMock.FindByIdWithProjectAsync(repository.Id, TestContext.Current.CancellationToken).Returns(repository);
        }
    }

    [Fact]
    public async Task UpdateRepositoryAsync_DesignatingAnotherConfigurationRepository_MakesItTheOne_AndTheOldOneStopsBeingIt()
    {
        var wizards = ProjectRepo(2, configuration: true);
        var users = ProjectRepo(3, configuration: false);
        ProjectHolds(ProjectRepo(1, configuration: false), wizards, users);

        var result = await _sut.UpdateRepositoryAsync(
            3, new UpdateGitLightRepoRequest { IsConfiguration = true }, TestContext.Current.CancellationToken);

        Assert.True(result!.IsConfiguration);
        Assert.True(users.IsConfiguration);
        await _lightRepoMock.Received(1).ClearConfigurationFlagAsync(1, 3, TestContext.Current.CancellationToken);
        await _lightRepoMock.Received(1).SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UpdateRepositoryAsync_GivingUpTheConfigurationRole_LeavesTheProjectWithNone()
    {
        var wizards = ProjectRepo(2, configuration: true);
        ProjectHolds(ProjectRepo(1, configuration: false), wizards);

        var result = await _sut.UpdateRepositoryAsync(
            2, new UpdateGitLightRepoRequest { IsConfiguration = false }, TestContext.Current.CancellationToken);

        Assert.False(result!.IsConfiguration);
        Assert.False(wizards.IsConfiguration);
        await _lightRepoMock.DidNotReceive().ClearConfigurationFlagAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateRepositoryAsync_WithoutAConfigurationAnswer_LeavesTheRoleAsItIs()
    {
        var wizards = ProjectRepo(2, configuration: true);
        ProjectHolds(ProjectRepo(1, configuration: false), wizards);

        await _sut.UpdateRepositoryAsync(2, new UpdateGitLightRepoRequest { Description = "x" }, TestContext.Current.CancellationToken);

        Assert.True(wizards.IsConfiguration);
        await _lightRepoMock.DidNotReceive().ClearConfigurationFlagAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateRepositoryAsync_TheOnlySourceRepository_CannotBecomeTheConfigurationRepository()
    {
        var only = ProjectRepo(1, configuration: false);
        ProjectHolds(only, ProjectRepo(2, configuration: true));

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.UpdateRepositoryAsync(
            1, new UpdateGitLightRepoRequest { IsConfiguration = true }, TestContext.Current.CancellationToken));

        Assert.False(only.IsConfiguration);
        await _lightRepoMock.DidNotReceive().SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UpdateRepositoryAsync_TheMirrorOfAnExternalRepository_CannotBecomeTheConfigurationRepository()
    {
        var mirror = ProjectRepo(3, configuration: false, mirror: true);
        ProjectHolds(ProjectRepo(1, configuration: false), mirror);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.UpdateRepositoryAsync(
            3, new UpdateGitLightRepoRequest { IsConfiguration = true }, TestContext.Current.CancellationToken));

        Assert.False(mirror.IsConfiguration);
    }

    [Fact]
    public void BuildCloneUrl_ReturnsCorrectUrl()
    {
        var url = _sut.BuildCloneUrl(1, "my-repo");

        Assert.Equal("https://localhost:5301/git/1/my-repo.git", url);
    }

    // Prod: the backend sits behind the Apache TLS proxy, so the raw request scheme is http on the
    // loopback hop. The configured canonical URL (Aetheus:PublicApiBaseUrl) must win so the clone URL
    // is HTTPS regardless of the proxied request scheme.
    [Fact]
    public void BuildCloneUrl_ConfiguredPublicApiBaseUrl_WinsOverHttpRequestScheme()
    {
        var httpAccessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("127.0.0.1", 10026);
        httpAccessor.HttpContext.Returns(httpContext);

        var config = Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>();
        config["Aetheus:PublicApiBaseUrl"].Returns("https://aetheus-api.example.com");

        var sut = new GitLightService(
            _lightRepoMock,
            _gitRepoMock,
            _cliMock,
            _auditMock,
            Options.Create(new GitLightOptions { RepositoriesPath = "/tmp/test-repos" }),
            httpAccessor,
            config,
            _hubMock,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            new GitBranchProtectionService(
                _lightRepoMock, _cliMock, _auditMock,
                Options.Create(new GitLightOptions { RepositoriesPath = "/tmp/test-repos" })),
            Substitute.For<IGitBranchMovedAnnouncer>(),
            Substitute.For<ILogger<GitLightService>>(),
            TimeProvider.System);

        var url = sut.BuildCloneUrl(1, "my-repo");

        Assert.Equal("https://aetheus-api.example.com/git/1/my-repo.git", url);
    }

    [Fact]
    public void BuildCloneUrl_ConfiguredBaseWorksWithoutHttpContext()
    {
        var httpAccessor = Substitute.For<IHttpContextAccessor>();
        httpAccessor.HttpContext.Returns((HttpContext?)null);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitLight:CloneBaseUrl"] = "https://runner-reachable.example.test/"
            })
            .Build();
        var options = Options.Create(new GitLightOptions { RepositoriesPath = "/tmp/test-repos" });
        var sut = new GitLightService(
            _lightRepoMock,
            _gitRepoMock,
            _cliMock,
            _auditMock,
            options,
            httpAccessor,
            config,
            _hubMock,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            new GitBranchProtectionService(
                _lightRepoMock, _cliMock, _auditMock, options),
            Substitute.For<IGitBranchMovedAnnouncer>(),
            Substitute.For<ILogger<GitLightService>>(),
            TimeProvider.System);

        var url = sut.BuildCloneUrl(2, "toto-conformance");

        Assert.Equal(
            "https://runner-reachable.example.test/git/2/toto-conformance.git",
            url);
    }

    [Fact]
    public void ResolveDiskPath_ReturnsFullPath()
    {
        var path = _sut.ResolveDiskPath(1, "test");

        Assert.Contains("test.git", path);
        Assert.Contains("1", path);
    }

    [Fact]
    public async Task CreatePullRequestAsync_CreatesAndReturnsDto()
    {
        var entity = new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test", GitConnectionId = 10 };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _lightRepoMock.GetNextPrNumberAsync(10, TestContext.Current.CancellationToken).Returns(1);
        _cliMock.GetCommitsAsync(Arg.Any<string>(), "feature", 0, 1, ct: TestContext.Current.CancellationToken)
            .Returns([new GitLightCommitDto { Sha = "abc123", ShortSha = "abc123" }]);
        _gitRepoMock.AddPullRequestAsync(Arg.Any<PullRequest>(), TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask);

        var request = new CreateInternalPullRequestRequest
        {
            Title = "My PR",
            SourceBranch = "feature",
            TargetBranch = "main"
        };

        var result = await _sut.CreatePullRequestAsync(1, request, "testuser", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("My PR", result.Title);
        Assert.Equal(PullRequestStatus.Open, result.Status);
    }

    [Fact]
    public async Task MergePullRequestAsync_RepoNotFound_ReturnsNull()
    {
        _lightRepoMock.FindByIdAsync(99, TestContext.Current.CancellationToken)
            .Returns((GitInternalRepo?)null);

        var result = await _sut.MergePullRequestAsync(99, 1, "user", ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task ClosePullRequestAsync_SetsClosed()
    {
        var entity = new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test", GitConnectionId = 10 };
        var pr = new PullRequest
        {
            Id = 5,
            GitConnectionId = 10,
            ExternalId = 1,
            Title = "PR",
            SourceBranch = "feat",
            TargetBranch = "main",
            AuthorLogin = "user",
            Status = PullRequestStatus.Open,
            ExternalCreatedAt = DateTime.UtcNow
        };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _gitRepoMock.FindPullRequestByExternalIdAsync(10, 1, TestContext.Current.CancellationToken).Returns(pr);
        _gitRepoMock.SaveChangesAsync(TestContext.Current.CancellationToken).Returns(Task.CompletedTask);

        var result = await _sut.ClosePullRequestAsync(1, 1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(PullRequestStatus.Closed, result.Status);
    }

    private GitInternalRepo CreateRepoEntity(int id = 1, int projectId = 1) =>
        new() { Id = id, ProjectId = projectId, Name = "Test", Slug = "test" };

    [Fact]
    public async Task CreateBranchAsync_CallsCliAndAudit()
    {
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(CreateRepoEntity());

        await _sut.CreateBranchAsync(1, new CreateGitLightBranchRequest { Name = "feature", StartRef = "main" }, ct: TestContext.Current.CancellationToken);

        await _cliMock.Received(1).CreateBranchAsync(Arg.Any<string>(), "feature", "main", TestContext.Current.CancellationToken);
        await _auditMock.Received(1).LogAsync("CreatedBranch", "GitInternalRepo", 1, "feature", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateBranchAsync_NullEntity_Throws()
    {
        _lightRepoMock.FindByIdAsync(99, TestContext.Current.CancellationToken).Returns((GitInternalRepo?)null);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _sut.CreateBranchAsync(99, new CreateGitLightBranchRequest { Name = "x", StartRef = "main" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteBranchAsync_NotProtected_DeletesAndAudits()
    {
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(CreateRepoEntity());
        _lightRepoMock.GetBranchProtectionRulesAsync(1, TestContext.Current.CancellationToken).Returns([]);

        await _sut.DeleteBranchAsync(1, "feature", ct: TestContext.Current.CancellationToken);

        await _cliMock.Received(1).DeleteBranchAsync(Arg.Any<string>(), "feature", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeleteBranchAsync_ProtectedBranch_ThrowsInvalidOperation()
    {
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(CreateRepoEntity());
        _lightRepoMock.GetBranchProtectionRulesAsync(1, TestContext.Current.CancellationToken)
            .Returns([new BranchProtectionRule { Pattern = "main", PreventDeletion = true, GitInternalRepoId = 1 }]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.DeleteBranchAsync(1, "main", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetTagsAsync_RepoNotFound_ReturnsEmpty()
    {
        _lightRepoMock.FindByIdAsync(99, TestContext.Current.CancellationToken).Returns((GitInternalRepo?)null);

        var result = await _sut.GetTagsAsync(99, ct: TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTagsAsync_Found_ReturnsTags()
    {
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(CreateRepoEntity());
        _cliMock.GetTagsAsync(Arg.Any<string>(), TestContext.Current.CancellationToken)
            .Returns([new GitLightTagDto { Name = "v1.0" }]);

        var result = await _sut.GetTagsAsync(1, ct: TestContext.Current.CancellationToken);
        Assert.Single(result);
    }

    [Fact]
    public async Task CreateTagAsync_CallsCliAndAudit()
    {
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(CreateRepoEntity());

        await _sut.CreateTagAsync(1, new CreateGitLightTagRequest { Name = "v1.0", Ref = "main", Message = "release" }, ct: TestContext.Current.CancellationToken);

        await _cliMock.Received(1).CreateTagAsync(Arg.Any<string>(), "v1.0", "main", "release", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeleteTagAsync_CallsCliAndAudit()
    {
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(CreateRepoEntity());

        await _sut.DeleteTagAsync(1, "v1.0", ct: TestContext.Current.CancellationToken);

        await _cliMock.Received(1).DeleteTagAsync(Arg.Any<string>(), "v1.0", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetBranchProtectionRulesAsync_ReturnsMappedDtos()
    {
        _lightRepoMock.GetBranchProtectionRulesAsync(1, TestContext.Current.CancellationToken)
            .Returns([new BranchProtectionRule { Id = 1, GitInternalRepoId = 1, Pattern = "main", PreventDeletion = true, PreventForcePush = true }]);

        var result = await _sut.GetBranchProtectionRulesAsync(1, ct: TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal("main", result[0].Pattern);
    }

    [Fact]
    public async Task CreateBranchProtectionRuleAsync_CreatesAndReturnsDto()
    {
        _lightRepoMock.AddBranchProtectionRuleAsync(Arg.Any<BranchProtectionRule>(), TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask);
        _lightRepoMock.GetBranchProtectionRulesAsync(1, TestContext.Current.CancellationToken).Returns([]);

        var result = await _sut.CreateBranchProtectionRuleAsync(1, new CreateBranchProtectionRuleRequest
        {
            Pattern = "release/*",
            PreventDeletion = true,
            PreventForcePush = false,
            ProtectionLevel = BranchProtectionLevel.ProtectedExceptPipelines
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("release/*", result.Pattern);
        await _lightRepoMock.Received(1).AddBranchProtectionRuleAsync(Arg.Any<BranchProtectionRule>(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeleteBranchProtectionRuleAsync_Found_ReturnsTrue()
    {
        _lightRepoMock.FindBranchProtectionRuleAsync(1, TestContext.Current.CancellationToken)
            .Returns(new BranchProtectionRule { Id = 1, GitInternalRepoId = 1, Pattern = "main" });
        _lightRepoMock.RemoveBranchProtectionRuleAsync(Arg.Any<BranchProtectionRule>(), TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask);
        _lightRepoMock.GetBranchProtectionRulesAsync(1, TestContext.Current.CancellationToken).Returns([]);

        var result = await _sut.DeleteBranchProtectionRuleAsync(1, 1, ct: TestContext.Current.CancellationToken);
        Assert.True(result);
    }

    [Fact]
    public async Task DeleteBranchProtectionRuleAsync_NotFound_ReturnsFalse()
    {
        _lightRepoMock.FindBranchProtectionRuleAsync(99, TestContext.Current.CancellationToken)
            .Returns((BranchProtectionRule?)null);

        var result = await _sut.DeleteBranchProtectionRuleAsync(1, 99, ct: TestContext.Current.CancellationToken);
        Assert.False(result);
    }

    /// <summary>
    /// F-001. The caller is authorized on the repository in the route, never on the rule id, so a
    /// rule belonging to another repository must be refused here. Without this an admin of repo 1
    /// could delete the prevent-force-push and require-pull-request rules of repo 2, in a project
    /// they have no access to at all.
    /// </summary>
    [Fact]
    public async Task DeleteBranchProtectionRuleAsync_RuleOfAnotherRepository_RefusesAndDeletesNothing()
    {
        var foreignRule = new BranchProtectionRule { Id = 5, GitInternalRepoId = 2, Pattern = "main" };
        _lightRepoMock.FindBranchProtectionRuleAsync(5, TestContext.Current.CancellationToken).Returns(foreignRule);

        var result = await _sut.DeleteBranchProtectionRuleAsync(1, 5, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        await _lightRepoMock.DidNotReceive().RemoveBranchProtectionRuleAsync(
            Arg.Any<BranchProtectionRule>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsBranchProtectedAsync_WithMatchingRule_ReturnsTrue()
    {
        _lightRepoMock.GetBranchProtectionRulesAsync(1, TestContext.Current.CancellationToken)
            .Returns([new BranchProtectionRule { Pattern = "main", GitInternalRepoId = 1 }]);

        Assert.True(await _sut.IsBranchProtectedAsync(1, "main", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsBranchProtectedAsync_NoMatchingRule_ReturnsFalse()
    {
        _lightRepoMock.GetBranchProtectionRulesAsync(1, TestContext.Current.CancellationToken)
            .Returns([new BranchProtectionRule { Pattern = "main", GitInternalRepoId = 1 }]);

        Assert.False(await _sut.IsBranchProtectedAsync(1, "feature", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MergePullRequestAsync_Success_MergesAndReturnsPr()
    {
        var entity = new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test", GitConnectionId = 10 };
        var pr = new PullRequest
        {
            Id = 5,
            GitConnectionId = 10,
            ExternalId = 1,
            Title = "Merge PR",
            SourceBranch = "feat",
            TargetBranch = "main",
            AuthorLogin = "user",
            Status = PullRequestStatus.Open,
            ExternalCreatedAt = DateTime.UtcNow
        };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _gitRepoMock.FindPullRequestByExternalIdAsync(10, 1, TestContext.Current.CancellationToken).Returns(pr);
        _cliMock.MergeBranchesAsync(Arg.Any<string>(), "feat", "main", "user", "user@aetheus", TestContext.Current.CancellationToken)
            .Returns((true, "abc123", (string?)null));
        _gitRepoMock.SaveChangesAsync(TestContext.Current.CancellationToken).Returns(Task.CompletedTask);

        var result = await _sut.MergePullRequestAsync(1, 1, "user", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(PullRequestStatus.Merged, result.Status);
    }

    [Fact]
    public async Task MergePullRequestAsync_MergeFails_Throws()
    {
        var entity = new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test", GitConnectionId = 10 };
        var pr = new PullRequest
        {
            Id = 5,
            GitConnectionId = 10,
            ExternalId = 1,
            Title = "PR",
            SourceBranch = "feat",
            TargetBranch = "main",
            AuthorLogin = "user",
            Status = PullRequestStatus.Open,
            ExternalCreatedAt = DateTime.UtcNow
        };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _gitRepoMock.FindPullRequestByExternalIdAsync(10, 1, TestContext.Current.CancellationToken).Returns(pr);
        _cliMock.MergeBranchesAsync(Arg.Any<string>(), "feat", "main", "user", "user@aetheus", TestContext.Current.CancellationToken)
            .Returns((false, (string?)null, "conflict"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.MergePullRequestAsync(1, 1, "user", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MergePullRequestAsync_PrNotOpen_ReturnsNull()
    {
        var entity = new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test", GitConnectionId = 10 };
        var pr = new PullRequest
        {
            Id = 5,
            GitConnectionId = 10,
            ExternalId = 1,
            Title = "PR",
            SourceBranch = "feat",
            TargetBranch = "main",
            AuthorLogin = "user",
            Status = PullRequestStatus.Closed,
            ExternalCreatedAt = DateTime.UtcNow
        };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _gitRepoMock.FindPullRequestByExternalIdAsync(10, 1, TestContext.Current.CancellationToken).Returns(pr);

        var result = await _sut.MergePullRequestAsync(1, 1, "user", ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPullRequestDiffAsync_Success_ReturnsDiff()
    {
        var entity = new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test", GitConnectionId = 10 };
        var pr = new PullRequest
        {
            Id = 5,
            GitConnectionId = 10,
            ExternalId = 1,
            Title = "PR",
            SourceBranch = "feat",
            TargetBranch = "main",
            AuthorLogin = "user",
            Status = PullRequestStatus.Open,
            ExternalCreatedAt = DateTime.UtcNow
        };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _gitRepoMock.FindPullRequestByExternalIdAsync(10, 1, TestContext.Current.CancellationToken).Returns(pr);
        _cliMock.GetDiffAsync(Arg.Any<string>(), "main", "feat", TestContext.Current.CancellationToken)
            .Returns(new PullRequestDiffDto { FileDiffs = [new FileDiffDto { Path = "a.txt" }] });

        var result = await _sut.GetPullRequestDiffAsync(1, 1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result.FileDiffs);
    }

    [Fact]
    public async Task GetPullRequestDiffAsync_RepoNotFound_ReturnsEmpty()
    {
        _lightRepoMock.FindByIdAsync(99, TestContext.Current.CancellationToken).Returns((GitInternalRepo?)null);

        var result = await _sut.GetPullRequestDiffAsync(99, 1, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result.FileDiffs);
    }

    [Fact]
    public async Task ClosePullRequestAsync_PrNotOpen_ReturnsNull()
    {
        var entity = new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test", GitConnectionId = 10 };
        var pr = new PullRequest
        {
            Id = 5,
            GitConnectionId = 10,
            ExternalId = 1,
            Title = "PR",
            SourceBranch = "feat",
            TargetBranch = "main",
            AuthorLogin = "user",
            Status = PullRequestStatus.Merged,
            ExternalCreatedAt = DateTime.UtcNow
        };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _gitRepoMock.FindPullRequestByExternalIdAsync(10, 1, TestContext.Current.CancellationToken).Returns(pr);

        var result = await _sut.ClosePullRequestAsync(1, 1, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPullRequestsAsync_RepoFound_ReturnsPaginated()
    {
        var entity = new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test", GitConnectionId = 10 };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        var pr = new PullRequest
        {
            Id = 5,
            GitConnectionId = 10,
            ExternalId = 1,
            Title = "PR",
            SourceBranch = "feat",
            TargetBranch = "main",
            AuthorLogin = "user",
            Status = PullRequestStatus.Open,
            ExternalCreatedAt = DateTime.UtcNow
        };
        _gitRepoMock.GetPullRequestsPagedAsync(10, null, 1, 25, null, TestContext.Current.CancellationToken, null, false)
            .Returns(([pr], 1));

        var result = await _sut.GetPullRequestsAsync(1, new PullRequestPaginationRequest(), ct: TestContext.Current.CancellationToken);

        Assert.Single(result.Items);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task GetPullRequestsAsync_RepoNotFound_ReturnsEmpty()
    {
        _lightRepoMock.FindByIdAsync(99, TestContext.Current.CancellationToken).Returns((GitInternalRepo?)null);

        var result = await _sut.GetPullRequestsAsync(99, new PullRequestPaginationRequest(), ct: TestContext.Current.CancellationToken);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetPullRequestAsync_Found_ReturnsDto()
    {
        var entity = new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test", GitConnectionId = 10 };
        var pr = new PullRequest
        {
            Id = 5,
            GitConnectionId = 10,
            ExternalId = 1,
            Title = "PR",
            SourceBranch = "feat",
            TargetBranch = "main",
            AuthorLogin = "user",
            Status = PullRequestStatus.Open,
            ExternalCreatedAt = DateTime.UtcNow
        };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _gitRepoMock.FindPullRequestByExternalIdAsync(10, 1, TestContext.Current.CancellationToken).Returns(pr);

        var result = await _sut.GetPullRequestAsync(1, 1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("PR", result.Title);
    }

    [Fact]
    public async Task GetPullRequestAsync_NotFound_ReturnsNull()
    {
        var entity = new GitInternalRepo { Id = 1, ProjectId = 1, Slug = "test", GitConnectionId = 10 };
        _lightRepoMock.FindByIdAsync(1, TestContext.Current.CancellationToken).Returns(entity);
        _gitRepoMock.FindPullRequestByExternalIdAsync(10, 1, TestContext.Current.CancellationToken).Returns((PullRequest?)null);

        var result = await _sut.GetPullRequestAsync(1, 1, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }
}
