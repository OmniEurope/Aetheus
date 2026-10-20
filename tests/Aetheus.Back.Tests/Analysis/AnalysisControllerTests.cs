// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

public sealed class AnalysisControllerTests
{
    private readonly IAnalysisService _service = Substitute.For<IAnalysisService>();
    private readonly IAnalysisFindingDecisionService _decisions = Substitute.For<IAnalysisFindingDecisionService>();
    private readonly IAnalysisRunResultService _runResults = Substitute.For<IAnalysisRunResultService>();
    private readonly IPipelineRunService _runs = Substitute.For<IPipelineRunService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly AnalysisController _controller;

    public AnalysisControllerTests()
    {
        _authz.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(),
                ResourceType.Project,
                Arg.Any<int?>(),
                Arg.Any<Permission>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        _controller = new AnalysisController(_service, _decisions, _runResults, _runs, _authz)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Name, "analyst"), new Claim("ServerId", "7")],
                        "test"))
                }
            }
        };
    }

    [Fact]
    public async Task ScopedPolicies_DelegateGlobalAndOrganizationOperations()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new UpsertAnalysisPolicyRequest { Name = "Release gate" };
        var global = new AnalysisPolicyDto { Id = 1, Name = "Global" };
        var organization = new AnalysisPolicyDto { Id = 2, OrganizationId = 9, Name = "Organization" };
        _service.GetScopedPoliciesAsync(null, ct).Returns([global]);
        _service.GetScopedPoliciesAsync(9, ct).Returns([organization]);
        _service.CreateScopedPolicyAsync(null, request, ct).Returns(global);
        _service.CreateScopedPolicyAsync(9, request, ct).Returns(organization);
        _service.UpdateScopedPolicyAsync(null, 1, request, ct).Returns(global);
        _service.UpdateScopedPolicyAsync(9, 2, request, ct).Returns(organization);

        Assert.IsType<OkObjectResult>((await _controller.GetGlobalPolicies(ct)).Result);
        Assert.IsType<CreatedAtActionResult>((await _controller.CreateGlobalPolicy(request, ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.UpdateGlobalPolicy(1, request, ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetOrganizationPolicies(9, ct)).Result);
        Assert.IsType<CreatedAtActionResult>((await _controller.CreateOrganizationPolicy(9, request, ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.UpdateOrganizationPolicy(9, 2, request, ct)).Result);

        await _service.Received(2).GetScopedPoliciesAsync(Arg.Any<int?>(), ct);
    }

    [Fact]
    public async Task AgentEndpoints_RequireAnAssignedServer()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new PublishAnalysisReportRequest { ScannerKey = "scanner", ScannerName = "Scanner", ScannerVersion = "1" };

        _runs.IsServerAssignedToRunAsync(12, 7, ct).Returns(false);
        Assert.IsType<ForbidResult>((await _controller.PublishReport(12, request, ct)).Result);
        Assert.IsType<ForbidResult>((await _controller.GetRunGate(12, null, ct)).Result);
        await _service.DidNotReceive().PublishReportAsync(Arg.Any<int>(), Arg.Any<PublishAnalysisReportRequest>(), ct);

        _runs.IsServerAssignedToRunAsync(12, 7, ct).Returns(true);
        _service.PublishReportAsync(12, request, ct).Returns(new AnalysisReportDto { Id = 21 });
        _service.GetRunGateAsync(12, null, ct).Returns(new AnalysisRunGateDto { PipelineRunId = 12 });

        Assert.IsType<OkObjectResult>((await _controller.PublishReport(12, request, ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetRunGate(12, null, ct)).Result);
    }

    [Fact]
    public async Task RunGateResult_RequiresPipelineReadPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        _runs.GetPipelineIdForRunAsync(12, ct).Returns((int?)null);
        Assert.IsType<NotFoundResult>((await _controller.GetRunGateResult(12, ct)).Result);

        _runs.GetPipelineIdForRunAsync(12, ct).Returns(4);
        _authz.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 4, Permission.Read, ct)
            .Returns(false);
        Assert.IsType<ForbidResult>((await _controller.GetRunGateResult(12, ct)).Result);

        _authz.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 4, Permission.Read, ct)
            .Returns(true);
        _runResults.GetRunResultAsync(12, ct).Returns(new AnalysisRunGateDto { PipelineRunId = 12 });
        Assert.IsType<OkObjectResult>((await _controller.GetRunGateResult(12, ct)).Result);
    }

    /// <summary>A findings page of several runs needs every one of them readable.</summary>
    [Fact]
    public async Task R485_RunFindings_ForbidsWhenOneRunOfTheSetIsUnreadable()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new AnalysisRunFindingsRequest { RunIds = [12, 13] };
        _runs.GetPipelineIdForRunAsync(12, ct).Returns(4);
        _runs.GetPipelineIdForRunAsync(13, ct).Returns(5);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 4, Permission.Read, ct).Returns(true);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 5, Permission.Read, ct).Returns(false);

        Assert.IsType<ForbidResult>((await _controller.GetRunFindings(request, ct)).Result);
        await _runResults.DidNotReceiveWithAnyArgs().GetRunFindingsAsync(request, ct);

        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 5, Permission.Read, ct).Returns(true);
        _runResults.GetRunFindingsAsync(request, ct).Returns(new AnalysisRunFindingsPageDto { OpenCount = 3 });
        Assert.IsType<OkObjectResult>((await _controller.GetRunFindings(request, ct)).Result);
    }

    [Fact]
    public async Task ProjectReadEndpoints_ForbidBeforeCallingTheService()
    {
        var ct = TestContext.Current.CancellationToken;
        _authz.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(),
                ResourceType.Project,
                3,
                Permission.Read,
                ct)
            .Returns(false);

        Assert.IsType<ForbidResult>((await _controller.GetFindings(3, new AnalysisFindingPaginationRequest(), ct)).Result);
        Assert.IsType<ForbidResult>((await _controller.GetReports(3, new PaginationRequest(), ct)).Result);
        Assert.IsType<ForbidResult>((await _controller.GetProjectSummary(3, ct)).Result);
        Assert.IsType<ForbidResult>((await _controller.GetMetrics(3, new AnalysisMetricPaginationRequest(), ct)).Result);
        Assert.IsType<ForbidResult>((await _controller.GetComponents(3, new AnalysisComponentPaginationRequest(), ct)).Result);
        Assert.IsType<ForbidResult>((await _controller.GetTrackingStatus(3, ct)).Result);
        Assert.IsType<ForbidResult>((await _controller.GetVulnerabilities(3, new PaginationRequest(), ct)).Result);
        Assert.IsType<ForbidResult>((await _controller.GetPolicies(3, ct)).Result);
        Assert.IsType<ForbidResult>((await _controller.GetExceptions(3, ct)).Result);

        await _service.DidNotReceive().GetFindingsAsync(Arg.Any<int>(), Arg.Any<AnalysisFindingPaginationRequest>(), ct);
        await _service.DidNotReceive().GetReportsAsync(Arg.Any<int>(), Arg.Any<PaginationRequest>(), ct);
    }

    [Fact]
    public async Task ProjectReadEndpoints_ReturnAuthorizedResults()
    {
        var ct = TestContext.Current.CancellationToken;
        _service.GetFindingsAsync(3, Arg.Any<AnalysisFindingPaginationRequest>(), ct)
            .Returns(Page<AnalysisFindingDto>());
        _service.GetReportsAsync(3, Arg.Any<PaginationRequest>(), ct).Returns(Page<AnalysisReportDto>());
        _service.GetProjectSummaryAsync(3, ct).Returns(new AnalysisProjectSummaryDto { ProjectId = 3 });
        _service.GetMetricsAsync(3, Arg.Any<AnalysisMetricPaginationRequest>(), ct).Returns(Page<AnalysisMetricDto>());
        _service.GetComponentsAsync(3, Arg.Any<AnalysisComponentPaginationRequest>(), ct).Returns(Page<AnalysisComponentDto>());
        _service.GetTrackingStatusAsync(3, ct).Returns(new AnalysisTrackingStatusDto { ProjectId = 3 });
        _service.GetVulnerabilityObservationsAsync(3, Arg.Any<PaginationRequest>(), ct)
            .Returns(Page<AnalysisVulnerabilityObservationDto>());
        _service.GetPoliciesAsync(3, ct).Returns([]);
        _service.GetExceptionsAsync(3, ct).Returns([]);

        Assert.IsType<OkObjectResult>((await _controller.GetFindings(3, new AnalysisFindingPaginationRequest(), ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetReports(3, new PaginationRequest(), ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetProjectSummary(3, ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetMetrics(3, new AnalysisMetricPaginationRequest(), ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetComponents(3, new AnalysisComponentPaginationRequest(), ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetTrackingStatus(3, ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetVulnerabilities(3, new PaginationRequest(), ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetPolicies(3, ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetExceptions(3, ct)).Result);

        _service.GetTrackingStatusAsync(4, ct).Returns((AnalysisTrackingStatusDto?)null);
        Assert.IsType<NoContentResult>((await _controller.GetTrackingStatus(4, ct)).Result);
    }

    [Fact]
    public async Task FindingEndpoints_DistinguishMissingForbiddenAndAuthorizedFindings()
    {
        var ct = TestContext.Current.CancellationToken;
        _service.GetFindingProjectIdAsync(11, ct).Returns((int?)null);
        Assert.IsType<NotFoundResult>((await _controller.GetFinding(11, ct)).Result);
        Assert.IsType<NotFoundResult>((await _controller.GetFindingOccurrences(11, ct: ct)).Result);
        Assert.IsType<NotFoundResult>((await _controller.GetFindingDecisions(11, ct)).Result);
        Assert.IsType<NotFoundResult>((await _controller.CreateFindingDecision(
            11, new CreateAnalysisFindingDecisionRequest { Reason = "Accepted risk" }, ct)).Result);

        _service.GetFindingProjectIdAsync(12, ct).Returns(3);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 3, Permission.Read, ct)
            .Returns(false);
        Assert.IsType<ForbidResult>((await _controller.GetFinding(12, ct)).Result);
        Assert.IsType<ForbidResult>((await _controller.GetFindingOccurrences(12, ct: ct)).Result);
        Assert.IsType<ForbidResult>((await _controller.GetFindingDecisions(12, ct)).Result);

        _service.GetFindingProjectIdAsync(13, ct).Returns(4);
        _service.GetFindingAsync(13, ct).Returns(new AnalysisFindingDto { Id = 13, ProjectId = 4 });
        _service.GetFindingOccurrencesAsync(13, 25, ct).Returns([new AnalysisFindingOccurrenceDto { Id = 1 }]);
        _decisions.GetFindingDecisionsAsync(13, ct).Returns([new AnalysisFindingDecisionDto { Id = 1 }]);
        Assert.IsType<OkObjectResult>((await _controller.GetFinding(13, ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetFindingOccurrences(13, 25, ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetFindingDecisions(13, ct)).Result);
    }

    [Fact]
    public async Task R2_027_RevokeFindingDecision_IsRefusedLikeADecision_AndPassesTheActor()
    {
        var ct = TestContext.Current.CancellationToken;
        _service.GetFindingProjectIdAsync(11, ct).Returns((int?)null);
        Assert.IsType<NotFoundResult>(await _controller.RevokeFindingDecision(11, ct));

        _service.GetFindingProjectIdAsync(12, ct).Returns(3);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 3, Permission.Admin, ct)
            .Returns(false);
        Assert.IsType<ForbidResult>(await _controller.RevokeFindingDecision(12, ct));

        _service.GetFindingProjectIdAsync(13, ct).Returns(4);
        Assert.IsType<NoContentResult>(await _controller.RevokeFindingDecision(13, ct));
        await _decisions.Received(1).RevokeActiveFindingDecisionAsync(13, "analyst", ct);
        await _decisions.DidNotReceive().RevokeActiveFindingDecisionAsync(12, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdministrativeMutations_PropagateAuthenticatedActor()
    {
        var ct = TestContext.Current.CancellationToken;
        var policyRequest = new UpsertAnalysisPolicyRequest { Name = "Project gate" };
        var exceptionRequest = new CreateAnalysisPolicyExceptionRequest { Reason = "Approved temporary exception" };
        var decisionRequest = new CreateAnalysisFindingDecisionRequest { Reason = "Accepted after review" };
        _service.CreatePolicyAsync(3, policyRequest, ct).Returns(new AnalysisPolicyDto { Id = 1 });
        _service.UpdatePolicyAsync(3, 1, policyRequest, ct).Returns(new AnalysisPolicyDto { Id = 1 });
        _service.CreateExceptionAsync(3, exceptionRequest, "analyst", ct)
            .Returns(new AnalysisPolicyExceptionDto { Id = 2 });
        _service.GetFindingProjectIdAsync(13, ct).Returns(3);
        _decisions.CreateFindingDecisionAsync(13, decisionRequest, "analyst", ct)
            .Returns(new AnalysisFindingDecisionDto { Id = 3 });

        Assert.IsType<CreatedAtActionResult>((await _controller.CreatePolicy(3, policyRequest, ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.UpdatePolicy(3, 1, policyRequest, ct)).Result);
        Assert.IsType<CreatedAtActionResult>((await _controller.CreateException(3, exceptionRequest, ct)).Result);
        Assert.IsType<NoContentResult>(await _controller.RevokeException(3, 2, ct));
        Assert.IsType<CreatedAtActionResult>((await _controller.CreateFindingDecision(13, decisionRequest, ct)).Result);

        await _service.Received(1).CreateExceptionAsync(3, exceptionRequest, "analyst", ct);
        await _service.Received(1).RevokeExceptionAsync(3, 2, "analyst", ct);
        await _decisions.Received(1).CreateFindingDecisionAsync(13, decisionRequest, "analyst", ct);
    }

    [Fact]
    public async Task Portfolio_UsesTheAuthorizedProjectSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new AnalysisPortfolioPaginationRequest();
        _authz.GetAccessibleResourceIdsAsync(
                Arg.Any<ClaimsPrincipal>(),
                ResourceType.Project,
                Permission.Read,
                ct)
            .Returns([2, 5]);
        _service.GetPortfolioAsync(
                Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 2, 5 })),
                request,
                ct)
            .Returns(Page<AnalysisPortfolioRowDto>());

        var result = await _controller.GetPortfolio(request, ct);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    private static PaginatedResult<T> Page<T>() => new() { Page = 1, PageSize = 25 };
}
