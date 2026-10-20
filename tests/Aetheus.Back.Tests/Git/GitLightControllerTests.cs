// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class GitLightControllerTests
{
    private readonly IGitLightService _serviceMock = Substitute.For<IGitLightService>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly GitLightController _sut;

    public GitLightControllerTests()
    {
        _authzMock.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<ResourceType>(),
                Arg.Any<int?>(),
                Arg.Any<Permission>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        _serviceMock.GetProjectIdForRepoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);

        _sut = new GitLightController(_serviceMock, _authzMock)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Name, "testuser")], "test"))
                }
            }
        };
    }

    [Fact]
    public async Task GetRepositories_ReturnsOk()
    {
        _serviceMock.GetRepositoriesPageAsync(1, Arg.Any<List<int>?>(), Arg.Any<PaginationRequest>(), TestContext.Current.CancellationToken)
            .Returns(new PaginatedResult<GitLightRepoDto>
            {
                Items = [new GitLightRepoDto { Id = 1, Name = "Repo" }],
                TotalCount = 1
            });

        var result = await _sut.GetRepositories(1, new PaginationRequest(), TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(Assert.IsType<PaginatedResult<GitLightRepoDto>>(ok.Value).Items);
    }

    // --- No-filter (cross-project "list all accessible") - authorization scoping ---

    [Fact]
    public async Task GetRepositories_NoProjectId_Admin_ListsAllRepos()
    {
        // Admin: GetAccessibleResourceIdsAsync returns null (unrestricted) -> service is asked for all repos.
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, Permission.Read, Arg.Any<CancellationToken>())
            .Returns((List<int>?)null);
        _serviceMock.GetRepositoriesPageAsync(null, null, Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<GitLightRepoDto>
            {
                Items = [new GitLightRepoDto { Id = 1, Name = "A" }, new GitLightRepoDto { Id = 2, Name = "B" }],
                TotalCount = 2
            });

        var result = await _sut.GetRepositories(null, new PaginationRequest(), TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(2, Assert.IsType<PaginatedResult<GitLightRepoDto>>(ok.Value).Items.Count);
    }

    [Fact]
    public async Task GetRepositories_NoProjectId_NoAccessibleProjects_ReturnsEmpty_WithoutCallingService()
    {
        // Empty accessible-id list = user can read no project -> short-circuit to an empty list.
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(new List<int>());

        var result = await _sut.GetRepositories(null, new PaginationRequest(), TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(Assert.IsType<PaginatedResult<GitLightRepoDto>>(ok.Value).Items);
        await _serviceMock.DidNotReceive().GetRepositoriesPageAsync(
            Arg.Any<int?>(), Arg.Any<List<int>?>(), Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRepositories_NoProjectId_PartialAccess_ScopesToAccessibleProjectsExactly()
    {
        // A non-admin with access to projects [1,2] must have EXACTLY that id list forwarded to the
        // service (the security guarantee: no repos from projects outside the accessible set).
        var accessible = new List<int> { 1, 2 };
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(accessible);
        _serviceMock.GetRepositoriesPageAsync(null, Arg.Any<List<int>?>(), Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<GitLightRepoDto>
            {
                Items = [new GitLightRepoDto { Id = 5, Name = "InProj1" }],
                TotalCount = 1
            });

        var result = await _sut.GetRepositories(null, new PaginationRequest(), TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(Assert.IsType<PaginatedResult<GitLightRepoDto>>(ok.Value).Items);
        await _serviceMock.Received(1).GetRepositoriesPageAsync(
            null, Arg.Is<List<int>?>(l => l != null && l.SequenceEqual(accessible)),
            Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRepository_NotFound_Returns404()
    {
        _serviceMock.GetRepositoryAsync(99, TestContext.Current.CancellationToken)
            .Returns((GitLightRepoDto?)null);

        var result = await _sut.GetRepository(99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetRepository_Found_ReturnsOk()
    {
        _serviceMock.GetRepositoryAsync(1, TestContext.Current.CancellationToken)
            .Returns(new GitLightRepoDto { Id = 1, Name = "Repo" });

        var result = await _sut.GetRepository(1, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<GitLightRepoDto>(ok.Value);
    }

    [Fact]
    public async Task CreateRepository_Returns201()
    {
        var request = new CreateGitLightRepoRequest { ProjectId = 1, Name = "New" };
        _serviceMock.CreateRepositoryAsync(request, TestContext.Current.CancellationToken)
            .Returns(new GitLightRepoDto { Id = 1, Name = "New" });

        var result = await _sut.CreateRepository(request, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task UpdateRepository_NotFound_Returns404()
    {
        _serviceMock.UpdateRepositoryAsync(99, Arg.Any<UpdateGitLightRepoRequest>(), TestContext.Current.CancellationToken)
            .Returns((GitLightRepoDto?)null);

        var result = await _sut.UpdateRepository(99, new UpdateGitLightRepoRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task DeleteRepository_NotFound_Returns404()
    {
        _serviceMock.DeleteRepositoryAsync(99, TestContext.Current.CancellationToken).Returns(false);

        var result = await _sut.DeleteRepository(99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteRepository_Found_ReturnsNoContent()
    {
        _serviceMock.DeleteRepositoryAsync(1, TestContext.Current.CancellationToken).Returns(true);

        var result = await _sut.DeleteRepository(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task GetCommits_ReturnsOk()
    {
        _serviceMock.GetCommitsAsync(1, null, 1, 30, null, TestContext.Current.CancellationToken)
            .Returns(new PaginatedResult<GitLightCommitDto> { Items = [], TotalCount = 0, Page = 1, PageSize = 30 });

        var result = await _sut.GetCommits(1, null, null, 1, 30, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetBranches_ReturnsOk()
    {
        _serviceMock.GetBranchesAsync(1, TestContext.Current.CancellationToken)
            .Returns([]);

        var result = await _sut.GetBranches(1, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreatePullRequest_Returns201()
    {
        var request = new CreateInternalPullRequestRequest
        {
            Title = "PR",
            SourceBranch = "feat",
            TargetBranch = "main"
        };
        _serviceMock.CreatePullRequestAsync(1, request, "testuser", TestContext.Current.CancellationToken)
            .Returns(new InternalPullRequestDto { Id = 1, Number = 1, Title = "PR" });

        var result = await _sut.CreatePullRequest(1, request, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task MergePullRequest_NotFound_Returns404()
    {
        _serviceMock.MergePullRequestAsync(1, 99, "testuser", TestContext.Current.CancellationToken)
            .Returns((InternalPullRequestDto?)null);

        var result = await _sut.MergePullRequest(1, 99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetBlob_NotFound_Returns404()
    {
        _serviceMock.GetBlobAsync(1, "main", "file.txt", TestContext.Current.CancellationToken)
            .Returns((GitLightBlobDto?)null);

        var result = await _sut.GetBlob(1, "main", "file.txt", TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // --- UpdateRepository ---

    [Fact]
    public async Task UpdateRepository_Found_ReturnsOk()
    {
        _serviceMock.UpdateRepositoryAsync(1, Arg.Any<UpdateGitLightRepoRequest>(), TestContext.Current.CancellationToken)
            .Returns(new GitLightRepoDto { Id = 1, Name = "Updated" });

        var result = await _sut.UpdateRepository(1, new UpdateGitLightRepoRequest(), TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("Updated", ((GitLightRepoDto)ok.Value!).Name);
    }

    // --- Tags ---

    [Fact]
    public async Task GetTags_ReturnsOk()
    {
        _serviceMock.GetTagsAsync(1, TestContext.Current.CancellationToken).Returns([]);

        var result = await _sut.GetTags(1, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateTag_ReturnsNoContent()
    {
        _serviceMock.CreateTagAsync(1, Arg.Any<CreateGitLightTagRequest>(), TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask);

        var result = await _sut.CreateTag(1, new CreateGitLightTagRequest { Name = "v1.0" }, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteTag_ReturnsNoContent()
    {
        _serviceMock.DeleteTagAsync(1, "v1.0", TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask);

        var result = await _sut.DeleteTag(1, "v1.0", TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    // --- Branches create/delete ---

    [Fact]
    public async Task CreateBranch_ReturnsNoContent()
    {
        _serviceMock.CreateBranchAsync(1, Arg.Any<CreateGitLightBranchRequest>(), TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask);

        var result = await _sut.CreateBranch(1, new CreateGitLightBranchRequest { Name = "feat" }, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteBranch_ReturnsNoContent()
    {
        _serviceMock.DeleteBranchAsync(1, "feat", TestContext.Current.CancellationToken)
            .Returns(Task.CompletedTask);

        var result = await _sut.DeleteBranch(1, "feat", TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    // --- Tree ---

    [Fact]
    public async Task GetTree_ReturnsOk()
    {
        _serviceMock.GetTreeAsync(1, null, null, TestContext.Current.CancellationToken).Returns([]);

        var result = await _sut.GetTree(1, null, null, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // --- Blob ---

    [Fact]
    public async Task GetBlob_Found_ReturnsOk()
    {
        _serviceMock.GetBlobAsync(1, "main", "f.txt", TestContext.Current.CancellationToken)
            .Returns(new GitLightBlobDto { Content = "hello" });

        var result = await _sut.GetBlob(1, "main", "f.txt", TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetBlobRaw_Found_ReturnsFileStream()
    {
        _serviceMock.GetBlobStreamAsync(1, "main", "f.txt", TestContext.Current.CancellationToken)
            .Returns(new MemoryStream([1, 2, 3]));

        var result = await _sut.GetBlobRaw(1, "main", "f.txt", TestContext.Current.CancellationToken);

        Assert.IsType<FileStreamResult>(result);
    }

    [Fact]
    public async Task GetBlobRaw_NotFound_Returns404()
    {
        _serviceMock.GetBlobStreamAsync(1, "main", "none", TestContext.Current.CancellationToken)
            .Returns((Stream?)null);

        var result = await _sut.GetBlobRaw(1, "main", "none", TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    // --- Pull Requests ---

    [Fact]
    public async Task GetPullRequests_ReturnsOk()
    {
        _serviceMock.GetPullRequestsAsync(1, Arg.Any<PullRequestPaginationRequest>(), TestContext.Current.CancellationToken)
            .Returns(new PaginatedResult<InternalPullRequestDto> { Items = [], TotalCount = 0 });

        var result = await _sut.GetPullRequests(1, new PullRequestPaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetPullRequest_Found_ReturnsOk()
    {
        _serviceMock.GetPullRequestAsync(1, 1, TestContext.Current.CancellationToken)
            .Returns(new InternalPullRequestDto { Id = 1, Number = 1 });

        var result = await _sut.GetPullRequest(1, 1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetPullRequest_NotFound_Returns404()
    {
        _serviceMock.GetPullRequestAsync(1, 99, TestContext.Current.CancellationToken)
            .Returns((InternalPullRequestDto?)null);

        var result = await _sut.GetPullRequest(1, 99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task MergePullRequest_Found_ReturnsOk()
    {
        _serviceMock.MergePullRequestAsync(1, 1, "testuser", TestContext.Current.CancellationToken)
            .Returns(new InternalPullRequestDto { Id = 1, Number = 1 });

        var result = await _sut.MergePullRequest(1, 1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task ClosePullRequest_Found_ReturnsOk()
    {
        _serviceMock.ClosePullRequestAsync(1, 1, TestContext.Current.CancellationToken)
            .Returns(new InternalPullRequestDto { Id = 1 });

        var result = await _sut.ClosePullRequest(1, 1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task ClosePullRequest_NotFound_Returns404()
    {
        _serviceMock.ClosePullRequestAsync(1, 99, TestContext.Current.CancellationToken)
            .Returns((InternalPullRequestDto?)null);

        var result = await _sut.ClosePullRequest(1, 99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetPullRequestDiff_ReturnsOk()
    {
        _serviceMock.GetPullRequestDiffAsync(1, 1, TestContext.Current.CancellationToken)
            .Returns(new PullRequestDiffDto());

        var result = await _sut.GetPullRequestDiff(1, 1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // --- Blame ---

    [Fact]
    public async Task GetBlame_ReturnsOk()
    {
        _serviceMock.GetBlameAsync(1, "main", "f.txt", TestContext.Current.CancellationToken)
            .Returns([]);

        var result = await _sut.GetBlame(1, "main", "f.txt", TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // --- Graph ---

    [Fact]
    public async Task GetCommitGraph_ReturnsOk()
    {
        _serviceMock.GetCommitGraphAsync(1, 100, TestContext.Current.CancellationToken)
            .Returns("graph output");

        var result = await _sut.GetCommitGraph(1, 100, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("graph output", ok.Value);
    }

    // --- Branch Protection ---

    [Fact]
    public async Task GetBranchProtectionRules_ReturnsOk()
    {
        _serviceMock.GetBranchProtectionRulesAsync(1, TestContext.Current.CancellationToken).Returns([]);

        var result = await _sut.GetBranchProtectionRules(1, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateBranchProtectionRule_ReturnsOk()
    {
        _serviceMock.CreateBranchProtectionRuleAsync(1, Arg.Any<CreateBranchProtectionRuleRequest>(), TestContext.Current.CancellationToken)
            .Returns(new BranchProtectionRuleDto { Id = 1 });

        var result = await _sut.CreateBranchProtectionRule(1, new CreateBranchProtectionRuleRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateBranchProtectionRule_Found_ReturnsOk()
    {
        _serviceMock.UpdateBranchProtectionRuleAsync(1, 1, Arg.Any<UpdateBranchProtectionRuleRequest>(), TestContext.Current.CancellationToken)
            .Returns(new BranchProtectionRuleDto { Id = 1 });

        var result = await _sut.UpdateBranchProtectionRule(1, 1, new UpdateBranchProtectionRuleRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateBranchProtectionRule_NotFound_Returns404()
    {
        _serviceMock.UpdateBranchProtectionRuleAsync(1, 99, Arg.Any<UpdateBranchProtectionRuleRequest>(), TestContext.Current.CancellationToken)
            .Returns((BranchProtectionRuleDto?)null);

        var result = await _sut.UpdateBranchProtectionRule(1, 99, new UpdateBranchProtectionRuleRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task DeleteBranchProtectionRule_Found_ReturnsNoContent()
    {
        _serviceMock.DeleteBranchProtectionRuleAsync(1, 1, TestContext.Current.CancellationToken).Returns(true);

        var result = await _sut.DeleteBranchProtectionRule(1, 1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteBranchProtectionRule_NotFound_Returns404()
    {
        _serviceMock.DeleteBranchProtectionRuleAsync(1, 99, TestContext.Current.CancellationToken).Returns(false);

        var result = await _sut.DeleteBranchProtectionRule(1, 99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    // --- Commit messages ---

    [Fact]
    public async Task GetCommitMessages_ValidShas_ReturnsTheMessagesTheServiceRead()
    {
        var sha = new string('a', 40);
        var messages = new Dictionary<string, string> { [sha] = "fix: something" };
        _serviceMock.GetCommitMessagesAsync(1, Arg.Is<IReadOnlyCollection<string>>(s => s.Single() == sha), Arg.Any<CancellationToken>())
            .Returns(messages);

        var result = await _sut.GetCommitMessages(1, new GitCommitMessagesRequest { Shas = [sha] }, TestContext.Current.CancellationToken);

        Assert.Same(messages, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    [InlineData(-1)]
    public async Task GetCommitMessages_NoneTooManyOrNonHexShas_IsAValidationProblem(int count)
    {
        _sut.ControllerContext.HttpContext.RequestServices = new ServiceCollection()
            .AddLogging().AddMvcCore().Services.BuildServiceProvider();
        List<string> shas = count < 0 ? ["not-a-sha"] : [.. Enumerable.Repeat(new string('b', 40), count)];

        var result = await _sut.GetCommitMessages(1, new GitCommitMessagesRequest { Shas = shas }, TestContext.Current.CancellationToken);

        Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        await _serviceMock.DidNotReceiveWithAnyArgs().GetCommitMessagesAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetCommitMessages_UnknownRepository_ReturnsNotFound()
    {
        _serviceMock.GetProjectIdForRepoAsync(9, Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _sut.GetCommitMessages(9, new GitCommitMessagesRequest { Shas = [new string('c', 40)] }, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetCommitMessages_WithoutReadPermission_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetCommitMessages(1, new GitCommitMessagesRequest { Shas = [new string('d', 40)] }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }
}
