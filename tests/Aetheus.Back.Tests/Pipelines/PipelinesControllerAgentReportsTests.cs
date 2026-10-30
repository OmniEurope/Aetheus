// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Only the agent assigned to a run publishes its complexity and lint reports; the artifact
/// service says whether the run took them. A reader gets a pipeline's source, a writer its run
/// parameters for a valid branch.</summary>
public sealed class PipelinesControllerAgentReportsTests
{
    private readonly IPipelineService _service = Substitute.For<IPipelineService>();
    private readonly IPipelineRunService _runs = Substitute.For<IPipelineRunService>();
    private readonly IPipelineArtifactService _artifacts = Substitute.For<IPipelineArtifactService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly PublishComplexityRequest _complexity = new();
    private readonly PublishLintRequest _lint = new();

    private PipelinesController Build(params Claim[] claims) => new(
        _service, _runs, Substitute.For<IPipelineApprovalService>(), _artifacts,
        Substitute.For<IPipelineWebhookService>(), new PipelineOwnerAuthorization(_service, _authz), _authz,
        Substitute.For<IPipelineRunToEndPolicy>())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "agent")) }
        }
    };

    private PipelinesController Agent() => Build(new Claim("ServerId", "4"));

    private void Allow(Permission permission, bool allowed) =>
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 3, permission, Arg.Any<CancellationToken>()).Returns(allowed);

    // R-11: the run dialog's parameters belong to running the pipeline, not to editing it.
    private void AllowRun(bool allowed) =>
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), PermissionKeys.PipelineRun, 3, Arg.Any<CancellationToken>()).Returns(allowed);

    [Theory]
    [InlineData(true, typeof(OkResult))]
    [InlineData(false, typeof(NotFoundResult))]
    public async Task Complexity_FromTheAssignedAgent_IsPublishedAndTheRunSaysWhetherItTookIt(bool stored, Type expected)
    {
        _runs.IsServerAssignedToRunAsync(9, 4, Arg.Any<CancellationToken>()).Returns(true);
        _artifacts.PublishComplexityAsync(9, _complexity, Arg.Any<CancellationToken>()).Returns(stored);

        var result = await Agent().PublishComplexity(9, _complexity, TestContext.Current.CancellationToken);

        Assert.IsType(expected, result);
        await _artifacts.Received(1).PublishComplexityAsync(9, _complexity, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Complexity_FromAnAgentNotOnTheRun_IsForbiddenAndPublishesNothing()
    {
        _runs.IsServerAssignedToRunAsync(9, 4, Arg.Any<CancellationToken>()).Returns(false);

        var result = await Agent().PublishComplexity(9, _complexity, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
        await _artifacts.DidNotReceiveWithAnyArgs().PublishComplexityAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Complexity_WithoutServerId_IsForbiddenBeforeAnyLookup()
    {
        var result = await Build().PublishComplexity(9, _complexity, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
        await _runs.DidNotReceiveWithAnyArgs().IsServerAssignedToRunAsync(default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Lint_FromTheAssignedAgent_ReturnsTheSummary()
    {
        _runs.IsServerAssignedToRunAsync(9, 4, Arg.Any<CancellationToken>()).Returns(true);
        var summary = new PipelineLintSummaryDto();
        _artifacts.PublishLintAsync(9, _lint, Arg.Any<CancellationToken>()).Returns(summary);

        var result = await Agent().PublishLint(9, _lint, TestContext.Current.CancellationToken);

        Assert.Same(summary, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Lint_ForARunThatDoesNotTakeIt_IsNotFound()
    {
        _runs.IsServerAssignedToRunAsync(9, 4, Arg.Any<CancellationToken>()).Returns(true);
        _artifacts.PublishLintAsync(9, _lint, Arg.Any<CancellationToken>()).Returns((PipelineLintSummaryDto?)null);

        Assert.IsType<NotFoundResult>((await Agent().PublishLint(9, _lint, TestContext.Current.CancellationToken)).Result);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("4", false)]
    public async Task Lint_WithoutServerIdOrFromAnotherAgent_IsForbidden(string? serverId, bool assigned)
    {
        _runs.IsServerAssignedToRunAsync(9, 4, Arg.Any<CancellationToken>()).Returns(assigned);
        var sut = serverId is null ? Build() : Build(new Claim("ServerId", serverId));

        var result = await sut.PublishLint(9, _lint, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _artifacts.DidNotReceiveWithAnyArgs().PublishLintAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Source_IsForbiddenToANonReader()
    {
        Allow(Permission.Read, false);

        Assert.IsType<ForbidResult>((await Agent().GetSource(3, TestContext.Current.CancellationToken)).Result);
        await _service.DidNotReceiveWithAnyArgs().GetPipelineSourceByIdAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Source_OfAnUnknownPipeline_IsNotFound()
    {
        Allow(Permission.Read, true);
        _service.GetPipelineSourceByIdAsync(3, Arg.Any<CancellationToken>()).Returns((false, (PipelineSourceDto?)null));

        Assert.IsType<NotFoundResult>((await Agent().GetSource(3, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task Source_OfAPipelineWithoutOne_IsNoContent()
    {
        Allow(Permission.Read, true);
        _service.GetPipelineSourceByIdAsync(3, Arg.Any<CancellationToken>()).Returns((true, (PipelineSourceDto?)null));

        Assert.IsType<NoContentResult>((await Agent().GetSource(3, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task Source_IsReturnedToAReader()
    {
        Allow(Permission.Read, true);
        var source = new PipelineSourceDto();
        _service.GetPipelineSourceByIdAsync(3, Arg.Any<CancellationToken>()).Returns((true, source));

        Assert.Same(source, Assert.IsType<OkObjectResult>((await Agent().GetSource(3, TestContext.Current.CancellationToken)).Result).Value);
    }

    [Fact]
    public async Task Parameters_AreForbiddenWithoutTheRunPermission()
    {
        AllowRun(false);

        Assert.IsType<ForbidResult>((await Agent().GetRunParameters(3, null, TestContext.Current.CancellationToken)).Result);
        await _runs.DidNotReceiveWithAnyArgs().GetRunParametersAsync(default, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("-evil")]
    [InlineData("feature/../main")]
    [InlineData("a b")]
    public async Task Parameters_ForAnInvalidBranch_AreABadRequest(string branch)
    {
        AllowRun(true);

        Assert.IsType<BadRequestObjectResult>((await Agent().GetRunParameters(3, branch, TestContext.Current.CancellationToken)).Result);
        await _runs.DidNotReceiveWithAnyArgs().GetRunParametersAsync(default, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("release/1.2")]
    public async Task Parameters_ForTheDefaultOrAValidBranch_AreRead(string? branch)
    {
        AllowRun(true);
        List<PipelineRunParameterDto> parameters = [new PipelineRunParameterDto()];
        _runs.GetRunParametersAsync(3, branch, Arg.Any<CancellationToken>()).Returns(parameters);

        var result = await Agent().GetRunParameters(3, branch, TestContext.Current.CancellationToken);

        Assert.Same(parameters, result.Value);
    }
}
