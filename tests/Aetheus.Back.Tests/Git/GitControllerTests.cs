// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class GitControllerTests
{
    private readonly IGitService _serviceMock = Substitute.For<IGitService>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly GitController _sut;

    public GitControllerTests()
    {
        _serviceMock.GetProjectIdForConnectionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);
        _serviceMock.GetProjectIdForBranchPolicyAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);
        _serviceMock.GetProjectIdForRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);

        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _sut = new GitController(_serviceMock, _authzMock);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
            }
        };
    }

    [Fact]
    public async Task GetConnections_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetConnectionsByProjectAsync(1, Arg.Any<CancellationToken>())
            .Returns([new GitConnectionDto { Id = 1, OwnerOrGroup = "myorg", RepositoryName = "repo" }]);

        var result = await _sut.GetConnections(1, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single((List<GitConnectionDto>)ok.Value!);
    }

    [Fact]
    public async Task GetConnections_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetConnections(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetConnection_Found_ReturnsOk()
    {
        _serviceMock.GetConnectionDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new GitConnectionDto { Id = 1, OwnerOrGroup = "myorg", RepositoryName = "repo" });

        var result = await _sut.GetConnection(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetConnection_NotFound_ReturnsNotFound()
    {
        _serviceMock.GetConnectionDetailAsync(999, Arg.Any<CancellationToken>())
            .Returns((GitConnectionDto?)null);

        var result = await _sut.GetConnection(999, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateConnection_Authorized_ReturnsCreated()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.CreateConnectionAsync(Arg.Any<CreateGitConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GitConnectionDto { Id = 1, OwnerOrGroup = "myorg", RepositoryName = "repo" });

        var result = await _sut.CreateConnection(new CreateGitConnectionRequest { ProjectId = 1, OwnerOrGroup = "myorg", RepositoryName = "repo", ProviderType = GitProviderType.GitHub }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task CreateConnection_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.CreateConnection(new CreateGitConnectionRequest { ProjectId = 1, OwnerOrGroup = "myorg", RepositoryName = "repo", ProviderType = GitProviderType.GitHub }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task UpdateConnection_Found_ReturnsOk()
    {
        _serviceMock.UpdateConnectionAsync(1, Arg.Any<UpdateGitConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GitConnectionDto { Id = 1, OwnerOrGroup = "myorg", RepositoryName = "updated" });

        var result = await _sut.UpdateConnection(1, new UpdateGitConnectionRequest { AutoSyncEnabled = true }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateConnection_NotFound_ReturnsNotFound()
    {
        _serviceMock.UpdateConnectionAsync(999, Arg.Any<UpdateGitConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns((GitConnectionDto?)null);

        var result = await _sut.UpdateConnection(999, new UpdateGitConnectionRequest { AutoSyncEnabled = false }, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task DeleteConnection_Deleted_ReturnsNoContent()
    {
        _serviceMock.DeleteConnectionAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeleteConnection(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteConnection_NotFound_ReturnsNotFound()
    {
        _serviceMock.DeleteConnectionAsync(999, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.DeleteConnection(999, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetPullRequests_ReturnsOk()
    {
        _serviceMock.GetPullRequestsAsync(1, Arg.Any<PullRequestPaginationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<PullRequestDto> { Items = [], TotalCount = 0 });

        var result = await _sut.GetPullRequests(1, new PullRequestPaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetBranchPolicies_ReturnsOk()
    {
        _serviceMock.GetBranchPoliciesAsync(1, Arg.Any<CancellationToken>())
            .Returns([new BranchPolicyDto { Id = 1 }]);

        var result = await _sut.GetBranchPolicies(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateBranchPolicy_ReturnsCreated()
    {
        _serviceMock.CreateBranchPolicyAsync(Arg.Any<CreateBranchPolicyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BranchPolicyDto { Id = 1 });

        var result = await _sut.CreateBranchPolicy(new CreateBranchPolicyRequest { GitConnectionId = 1, BranchPattern = "main", PolicyType = BranchPolicyType.RequirePullRequest }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task DeleteBranchPolicy_Deleted_ReturnsNoContent()
    {
        _serviceMock.DeleteBranchPolicyAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeleteBranchPolicy(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task ReportPipelineStatus_ReturnsOk_WithHonestLocalOnlyBody()
    {
        var result = await _sut.ReportPipelineStatus(new PipelineStatusReport { PipelineRunId = 1, State = "success", Context = "aetheus-ci" }, TestContext.Current.CancellationToken);

        // Audit P-43: the body must be honest that the status is recorded locally only, not forwarded
        // to the external provider (no fake "reported" 200).
        var ok = Assert.IsType<OkObjectResult>(result);
        var forwarded = ok.Value!.GetType().GetProperty("forwardedToProvider")!.GetValue(ok.Value);
        Assert.Equal(false, forwarded);
        await _serviceMock.Received(1).ReportPipelineStatusAsync(Arg.Any<PipelineStatusReport>(), Arg.Any<CancellationToken>());
    }

    // --- Pull request sync and branch policy update ---

    [Fact]
    public async Task SyncPullRequest_OnAnUnknownConnection_IsNotFound()
    {
        _serviceMock.GetProjectIdForConnectionAsync(9, Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _sut.SyncPullRequest(9, new PullRequestDto { ExternalId = 4 }, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
        await _serviceMock.DidNotReceiveWithAnyArgs().SyncPullRequestAsync(default, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SyncPullRequest_WithoutWriteOnTheProject_IsForbidden()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.SyncPullRequest(2, new PullRequestDto { ExternalId = 4 }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceiveWithAnyArgs().SyncPullRequestAsync(default, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SyncPullRequest_StoresItUnderItsExternalId()
    {
        var incoming = new PullRequestDto { ExternalId = 4 };
        var stored = new PullRequestDto { Id = 8, ExternalId = 4 };
        _serviceMock.SyncPullRequestAsync(2, 4, incoming, Arg.Any<CancellationToken>()).Returns(stored);

        var result = await _sut.SyncPullRequest(2, incoming, TestContext.Current.CancellationToken);

        Assert.Same(stored, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task SyncPullRequest_TheServiceRefuses_IsABadRequest()
    {
        _serviceMock.SyncPullRequestAsync(2, 4, Arg.Any<PullRequestDto>(), Arg.Any<CancellationToken>()).Returns((PullRequestDto?)null);

        var result = await _sut.SyncPullRequest(2, new PullRequestDto { ExternalId = 4 }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestResult>(result.Result);
    }

    [Fact]
    public async Task UpdateBranchPolicy_OfAnUnknownPolicy_IsNotFound()
    {
        _serviceMock.GetProjectIdForBranchPolicyAsync(6, Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _sut.UpdateBranchPolicy(6, new UpdateBranchPolicyRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
        await _serviceMock.DidNotReceiveWithAnyArgs().UpdateBranchPolicyAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UpdateBranchPolicy_WithoutWriteOnTheProject_IsForbidden()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.UpdateBranchPolicy(6, new UpdateBranchPolicyRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceiveWithAnyArgs().UpdateBranchPolicyAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UpdateBranchPolicy_ReturnsTheUpdatedPolicy_OrNotFoundWhenItVanished()
    {
        var request = new UpdateBranchPolicyRequest();
        var updated = new BranchPolicyDto { Id = 6 };
        _serviceMock.UpdateBranchPolicyAsync(6, request, Arg.Any<CancellationToken>()).Returns(updated);
        _serviceMock.UpdateBranchPolicyAsync(7, request, Arg.Any<CancellationToken>()).Returns((BranchPolicyDto?)null);

        Assert.Same(updated, Assert.IsType<OkObjectResult>((await _sut.UpdateBranchPolicy(6, request, TestContext.Current.CancellationToken)).Result).Value);
        Assert.IsType<NotFoundResult>((await _sut.UpdateBranchPolicy(7, request, TestContext.Current.CancellationToken)).Result);
    }
}
