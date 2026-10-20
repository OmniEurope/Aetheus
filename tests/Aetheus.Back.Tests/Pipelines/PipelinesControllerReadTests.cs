// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class PipelinesControllerReadTests
{
    private readonly IPipelineRunService _runService = Substitute.For<IPipelineRunService>();
    private readonly IPipelineArtifactService _artifactService = Substitute.For<IPipelineArtifactService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IPipelineRunLineageService _lineage = Substitute.For<IPipelineRunLineageService>();
    private readonly PipelinesController _sut;

    [Fact]
    public async Task R498_GetRunLineage_IsReadOnlyByThoseWhoMayReadThePipeline()
    {
        var ct = TestContext.Current.CancellationToken;
        _runService.GetPipelineIdForRunAsync(7, ct).Returns(5);
        _lineage.GetLineageAsync(7, ct).Returns(new PipelineRunLineageDto { TriggeredBy = "sony" });

        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 5, Permission.Read, ct).Returns(false);
        var sut = new PipelineRunLineageController(_runService, _lineage, _authz) { ControllerContext = _sut.ControllerContext };
        Assert.IsType<ForbidResult>((await sut.GetRunLineage(7, ct)).Result);
        await _lineage.DidNotReceive().GetLineageAsync(7, ct);

        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 5, Permission.Read, ct).Returns(true);
        var ok = Assert.IsType<OkObjectResult>((await sut.GetRunLineage(7, ct)).Result);
        Assert.Equal("sony", Assert.IsType<PipelineRunLineageDto>(ok.Value).TriggeredBy);

        _runService.GetPipelineIdForRunAsync(404, ct).Returns((int?)null);
        Assert.IsType<NotFoundResult>((await sut.GetRunLineage(404, ct)).Result);
    }

    public PipelinesControllerReadTests()
    {
        var service = Substitute.For<IPipelineService>();
        _sut = new PipelinesController(service, _runService,
            Substitute.For<IPipelineApprovalService>(), _artifactService,
            Substitute.For<IPipelineWebhookService>(), new PipelineOwnerAuthorization(service, _authz), _authz,
            Substitute.For<IPipelineRunToEndPolicy>());
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
            }
        };
    }

    private void ResolvePipeline(int? pipelineId) =>
        _runService.GetPipelineIdForRunAsync(1, Arg.Any<CancellationToken>()).Returns(pipelineId);

    private void Allow(bool value) =>
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int>(),
            Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(value);

    [Fact]
    public async Task GetCoverage_RunUnknown_ReturnsNotFound()
    {
        ResolvePipeline(null);
        Assert.IsType<NotFoundResult>((await _sut.GetCoverage(1, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task GetProjectQualityTrend_Forbidden_ReturnsForbid()
    {
        Allow(false);
        Assert.IsType<ForbidResult>((await _sut.GetProjectQualityTrend(7, 15, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task GetProjectQualityTrend_Authorized_ReturnsOk()
    {
        Allow(true);
        _artifactService.GetProjectQualityTrendAsync(7, 15, Arg.Any<CancellationToken>()).Returns(new ProjectQualityTrendDto());

        Assert.IsType<OkObjectResult>((await _sut.GetProjectQualityTrend(7, 15, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task GetCoverage_Forbidden_ReturnsForbid()
    {
        ResolvePipeline(5);
        Allow(false);
        Assert.IsType<ForbidResult>((await _sut.GetCoverage(1, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task GetCoverage_Authorized_ReturnsOk()
    {
        ResolvePipeline(5);
        Allow(true);
        _artifactService.GetCoverageSummaryAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineCoverageSummaryDto());

        Assert.IsType<OkObjectResult>((await _sut.GetCoverage(1, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task GetCoverageTrend_Authorized_ReturnsOk()
    {
        ResolvePipeline(5);
        Allow(true);
        _artifactService.GetCoverageTrendAsync(1, 15, Arg.Any<CancellationToken>()).Returns([]);

        Assert.IsType<OkObjectResult>((await _sut.GetCoverageTrend(1, 15, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task GetMetricsTrend_Authorized_ReturnsOk()
    {
        ResolvePipeline(5);
        Allow(true);
        _artifactService.GetComplexityTrendAsync(1, 15, Arg.Any<CancellationToken>()).Returns([]);

        Assert.IsType<OkObjectResult>((await _sut.GetMetricsTrend(1, 15, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task GetLint_Authorized_ReturnsOk()
    {
        ResolvePipeline(5);
        Allow(true);
        _artifactService.GetLintSummaryAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineLintSummaryDto());

        Assert.IsType<OkObjectResult>((await _sut.GetLint(1, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task GetLint_SummaryNull_ReturnsNotFound()
    {
        ResolvePipeline(5);
        Allow(true);
        _artifactService.GetLintSummaryAsync(1, Arg.Any<CancellationToken>()).Returns((PipelineLintSummaryDto?)null);

        Assert.IsType<NotFoundResult>((await _sut.GetLint(1, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task GetArtifacts_Authorized_ReturnsOk()
    {
        ResolvePipeline(5);
        Allow(true);
        _artifactService.GetArtifactsAsync(1, Arg.Any<CancellationToken>()).Returns([]);

        Assert.IsType<OkObjectResult>((await _sut.GetArtifacts(1, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task GetApprovals_Authorized_ReturnsOk()
    {
        ResolvePipeline(5);
        Allow(true);

        Assert.IsType<OkObjectResult>((await _sut.GetApprovals(1, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task GetTestResults_Authorized_ReturnsOk()
    {
        ResolvePipeline(5);
        Allow(true);
        _artifactService.GetTestResultsAsync(1, Arg.Any<CancellationToken>()).Returns([]);

        Assert.IsType<OkObjectResult>((await _sut.GetTestResults(1, TestContext.Current.CancellationToken)).Result);
    }
}
