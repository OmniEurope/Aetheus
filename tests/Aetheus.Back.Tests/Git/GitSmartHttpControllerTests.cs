// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// Unit tests for <see cref="GitSmartHttpController"/>. Authentication concerns
/// (Basic / malformed headers / invalid credentials) live in
/// <see cref="GitBasicAuthenticationHandlerTests"/> since they are now enforced by
/// the dedicated authentication scheme rather than inline controller logic.
/// </summary>
public class GitSmartHttpControllerTests
{
    [Fact]
    public void ReceivePack_AllowsRepositorySizedRequestBodies()
    {
        var method = typeof(GitSmartHttpController).GetMethod(nameof(GitSmartHttpController.ReceivePack));
        var limit = method!.CustomAttributes
            .Single(attribute => attribute.AttributeType == typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute));

        Assert.Equal(512L * 1024 * 1024, (long)limit.ConstructorArguments.Single().Value!);
    }

    private readonly IGitSmartHttpService _serviceMock = Substitute.For<IGitSmartHttpService>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly GitSmartHttpController _sut;

    public GitSmartHttpControllerTests()
    {
        _authzMock.HasPermissionAsync(
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
                Arg.Any<ResourceType>(),
                Arg.Any<int?>(),
                Arg.Any<Permission>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        _sut = new GitSmartHttpController(_serviceMock, _authzMock)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    // --- GetInfoRefs ---

    [Fact]
    public async Task GetInfoRefs_ServiceReturnsNull_ReturnsNotFound()
    {
        _serviceMock.GetInfoRefsAsync(1, "repo", "git-upload-pack", Arg.Any<CancellationToken>())
            .Returns(((string, byte[])?)null);

        var result = await _sut.GetInfoRefs(1, "repo", "git-upload-pack", TestContext.Current.CancellationToken);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetInfoRefs_ServiceReturnsData_ReturnsFile()
    {
        var body = Encoding.UTF8.GetBytes("packdata");
        _serviceMock.GetInfoRefsAsync(1, "repo", "git-upload-pack", Arg.Any<CancellationToken>())
            .Returns(("application/x-git-upload-pack-advertisement", body));

        var result = await _sut.GetInfoRefs(1, "repo", "git-upload-pack", TestContext.Current.CancellationToken);
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/x-git-upload-pack-advertisement", fileResult.ContentType);
        Assert.Equal(body, fileResult.FileContents);
    }

    // --- UploadPack ---

    [Fact]
    public async Task UploadPack_ServiceReturnsNull_ReturnsNotFound()
    {
        _serviceMock.ExecuteServiceAsync(1, "repo", "git-upload-pack", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns((GitSmartHttpResponse?)null);

        var result = await _sut.UploadPack(1, "repo", TestContext.Current.CancellationToken);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task UploadPack_Success_ReturnsFileStream()
    {
        var stream = new MemoryStream([1, 2, 3]);
        _serviceMock.ExecuteServiceAsync(1, "repo", "git-upload-pack", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new GitSmartHttpResponse("application/x-git-upload-pack-result", stream, []));

        var result = await _sut.UploadPack(1, "repo", TestContext.Current.CancellationToken);
        var fileStream = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/x-git-upload-pack-result", fileStream.ContentType);
    }

    // --- ReceivePack ---

    [Fact]
    public async Task ReceivePack_ServiceReturnsNull_ReturnsNotFound()
    {
        _serviceMock.ExecuteServiceAsync(1, "repo", "git-receive-pack", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns((GitSmartHttpResponse?)null);

        var result = await _sut.ReceivePack(1, "repo", TestContext.Current.CancellationToken);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ReceivePack_Success_ReturnsFileStreamAndMarksPushed()
    {
        var stream = new MemoryStream([1, 2, 3]);
        _serviceMock.ExecuteServiceAsync(1, "repo", "git-receive-pack", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new GitSmartHttpResponse("application/x-git-receive-pack-result", stream, []));

        // The pusher is recorded with the push (RBAC audit, finding 3: an edited definition becomes hers).
        _sut.ControllerContext.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "carol")], "Test"));

        var result = await _sut.ReceivePack(1, "repo", TestContext.Current.CancellationToken);
        Assert.IsType<FileStreamResult>(result);
        await _serviceMock.Received(1).MarkPushedAsync(1, "repo", Arg.Any<IReadOnlyList<GitRefUpdate>>(), Arg.Any<CancellationToken>(), "carol");
    }

    // --- Run-token scope (GitRunCloneToken) honoured for READ only ---

    // A run-scoped principal with NO real user permission (authz denies everything) must still be
    // able to clone (UploadPack/info-refs read) the project it is scoped to, but never push.
    private GitSmartHttpController BuildRunScopedController(int scopedProjectId)
    {
        var denyAuthz = Substitute.For<IResourceAuthorizationService>();
        denyAuthz.HasPermissionAsync(
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(),
                Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var identity = new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(GitBasicAuthenticationHandler.RunScopeClaim, scopedProjectId.ToString())],
            GitBasicAuthenticationHandler.SchemeName);

        return new GitSmartHttpController(_serviceMock, denyAuthz)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) }
            }
        };
    }

    [Fact]
    public async Task UploadPack_RunScopedToken_BypassesPermissionForOwnProject()
    {
        var sut = BuildRunScopedController(scopedProjectId: 1);
        var stream = new MemoryStream([1, 2, 3]);
        _serviceMock.ExecuteServiceAsync(1, "repo", "git-upload-pack", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new GitSmartHttpResponse("application/x-git-upload-pack-result", stream, []));

        var result = await sut.UploadPack(1, "repo", TestContext.Current.CancellationToken);

        Assert.IsType<FileStreamResult>(result);
    }

    [Fact]
    public async Task UploadPack_RunScopedToken_DeniedForADifferentProject()
    {
        var sut = BuildRunScopedController(scopedProjectId: 1);

        // Scoped to project 1, but cloning project 2: no bypass, authz denies → Forbid.
        var result = await sut.UploadPack(2, "repo", TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task ReceivePack_RunScopedToken_IsForbidden()
    {
        var sut = BuildRunScopedController(scopedProjectId: 1);

        // A run token is read-only: push must never be allowed by the scope claim.
        var result = await sut.ReceivePack(1, "repo", TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetInfoRefs_RunScopedToken_ForbiddenForReceivePackService()
    {
        var sut = BuildRunScopedController(scopedProjectId: 1);

        // info/refs advertising the write service must not be unlocked by the read-only scope.
        var result = await sut.GetInfoRefs(1, "repo", "git-receive-pack", TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
    }
}
