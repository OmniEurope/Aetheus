// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Pending approvals are listed only for pipelines the caller may read.</summary>
public sealed class PipelineApprovalsControllerTests
{
    private readonly IPipelineApprovalService _service = Substitute.For<IPipelineApprovalService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly PipelineApprovalsController _sut;

    public PipelineApprovalsControllerTests()
    {
        _sut = new PipelineApprovalsController(_service, _authz)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([], "test")) }
            }
        };
    }

    [Fact]
    public async Task GetPending_ScopesTheListToReadablePipelines()
    {
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, Permission.Read, Arg.Any<CancellationToken>())
            .Returns([60]);
        _service.GetPendingApprovalsAsync(Arg.Is<List<int>?>(ids => ids != null && ids.SequenceEqual(new[] { 60 })), Arg.Any<CancellationToken>())
            .Returns([new PendingApprovalDto { ApprovalId = 1, PipelineRunId = 600 }]);

        var result = await _sut.GetPending(TestContext.Current.CancellationToken);

        var approval = Assert.Single(Assert.IsType<List<PendingApprovalDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal(600, approval.PipelineRunId);
    }

    [Fact]
    public async Task GetPending_WithNoReadablePipeline_ReturnsNothingWithoutQuerying()
    {
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, Permission.Read, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _sut.GetPending(TestContext.Current.CancellationToken);

        Assert.Empty(Assert.IsType<List<PendingApprovalDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
        await _service.DidNotReceive().GetPendingApprovalsAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>());
    }
}
