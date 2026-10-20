// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Components.Analysis;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class AnalysisController(
    IAnalysisService service,
    IAnalysisFindingDecisionService decisions,
    IAnalysisRunResultService runResults,
    IPipelineRunService pipelineRunService,
    IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet("policies/global")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<AnalysisPolicyDto>>> GetGlobalPolicies(CancellationToken ct) =>
        Ok(await service.GetScopedPoliciesAsync(null, ct));

    [HttpPost("policies/global")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<AnalysisPolicyDto>> CreateGlobalPolicy(
        [FromBody] UpsertAnalysisPolicyRequest request,
        CancellationToken ct)
    {
        var policy = await service.CreateScopedPolicyAsync(null, request, ct);
        return CreatedAtAction(nameof(GetGlobalPolicies), policy);
    }

    [HttpPut("policies/global/{policyId:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<AnalysisPolicyDto>> UpdateGlobalPolicy(
        int policyId,
        [FromBody] UpsertAnalysisPolicyRequest request,
        CancellationToken ct) =>
        Ok(await service.UpdateScopedPolicyAsync(null, policyId, request, ct));

    [HttpPost("policies/global/batch")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<AnalysisPolicyDto>>> ApplyGlobalPolicyBatch(
        [FromBody] ApplyAnalysisPolicyBatchRequest request,
        CancellationToken ct) =>
        Ok(await service.ApplyPolicyBatchAsync(null, null, request, ct));

    [HttpPost("policies/global/preview")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<AnalysisPolicySetPreviewDto>> PreviewGlobalPolicies(
        [FromBody] PreviewAnalysisPolicySetRequest request,
        CancellationToken ct) =>
        Ok(await service.PreviewPolicySetAsync(null, null, request, ct));

    [HttpGet("policies/global/{policyId:int}/revisions")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<AnalysisPolicyRevisionDto>>> GetGlobalPolicyRevisions(
        int policyId,
        CancellationToken ct) =>
        Ok(await service.GetPolicyRevisionsAsync(null, null, policyId, ct));

    [HttpPost("policies/global/{policyId:int}/rollback/{version:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<AnalysisPolicyDto>> RollbackGlobalPolicy(
        int policyId,
        int version,
        CancellationToken ct) =>
        Ok(await service.RollbackPolicyAsync(null, null, policyId, version, ct));

    [HttpGet("organizations/{organizationId:int}/policies")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<AnalysisPolicyDto>>> GetOrganizationPolicies(
        int organizationId,
        CancellationToken ct) =>
        Ok(await service.GetScopedPoliciesAsync(organizationId, ct));

    [HttpPost("organizations/{organizationId:int}/policies")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<AnalysisPolicyDto>> CreateOrganizationPolicy(
        int organizationId,
        [FromBody] UpsertAnalysisPolicyRequest request,
        CancellationToken ct)
    {
        var policy = await service.CreateScopedPolicyAsync(organizationId, request, ct);
        return CreatedAtAction(nameof(GetOrganizationPolicies), new { organizationId }, policy);
    }

    [HttpPut("organizations/{organizationId:int}/policies/{policyId:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<AnalysisPolicyDto>> UpdateOrganizationPolicy(
        int organizationId,
        int policyId,
        [FromBody] UpsertAnalysisPolicyRequest request,
        CancellationToken ct) =>
        Ok(await service.UpdateScopedPolicyAsync(organizationId, policyId, request, ct));

    [HttpPost("organizations/{organizationId:int}/policies/batch")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<AnalysisPolicyDto>>> ApplyOrganizationPolicyBatch(
        int organizationId,
        [FromBody] ApplyAnalysisPolicyBatchRequest request,
        CancellationToken ct) =>
        Ok(await service.ApplyPolicyBatchAsync(organizationId, null, request, ct));

    [HttpPost("organizations/{organizationId:int}/policies/preview")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<AnalysisPolicySetPreviewDto>> PreviewOrganizationPolicies(
        int organizationId,
        [FromBody] PreviewAnalysisPolicySetRequest request,
        CancellationToken ct) =>
        Ok(await service.PreviewPolicySetAsync(organizationId, null, request, ct));

    [HttpGet("organizations/{organizationId:int}/policies/{policyId:int}/revisions")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<AnalysisPolicyRevisionDto>>> GetOrganizationPolicyRevisions(
        int organizationId,
        int policyId,
        CancellationToken ct) =>
        Ok(await service.GetPolicyRevisionsAsync(organizationId, null, policyId, ct));

    [HttpPost("organizations/{organizationId:int}/policies/{policyId:int}/rollback/{version:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<AnalysisPolicyDto>> RollbackOrganizationPolicy(
        int organizationId,
        int policyId,
        int version,
        CancellationToken ct) =>
        Ok(await service.RollbackPolicyAsync(organizationId, null, policyId, version, ct));

    [HttpPost("runs/{runId:int}/reports")]
    [Authorize(Policy = "AgentToken")]
    [RequestSizeLimit(104_857_600)]
    [ServiceFilter(typeof(AnalysisReportAdmissionFilter))]
    public async Task<ActionResult<AnalysisReportDto>> PublishReport(
        int runId,
        [FromBody] PublishAnalysisReportRequest request,
        CancellationToken ct)
    {
        var serverIdClaim = User.FindFirst("ServerId")?.Value;
        if (!int.TryParse(serverIdClaim, out var serverId)) return Forbid();
        if (!await pipelineRunService.IsServerAssignedToRunAsync(runId, serverId, ct)) return Forbid();
        return Ok(await service.PublishReportAsync(runId, request, ct));
    }

    [HttpGet("runs/{runId:int}/gate")]
    [Authorize(Policy = "AgentToken")]
    public async Task<ActionResult<AnalysisRunGateDto>> GetRunGate(
        int runId,
        [FromQuery] string? scope,
        CancellationToken ct)
    {
        var serverIdClaim = User.FindFirst("ServerId")?.Value;
        if (!int.TryParse(serverIdClaim, out var serverId)) return Forbid();
        if (!await pipelineRunService.IsServerAssignedToRunAsync(runId, serverId, ct)) return Forbid();
        if (scope is not null && !AnalysisGateScopes.IsValid(scope))
            return BadRequest("scope must be security or quality.");
        return Ok(await service.GetRunGateAsync(runId, scope, ct));
    }

    [HttpGet("runs/{runId:int}/result")]
    public async Task<ActionResult<AnalysisRunGateDto>> GetRunGateResult(
        int runId,
        CancellationToken ct)
    {
        var pipelineId = await pipelineRunService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct))
            return Forbid();
        return Ok(await runResults.GetRunResultAsync(runId, ct));
    }

    /// <summary>One page of the findings a run and the runs it triggered observed, with the counts
    /// of the whole set. Every run of the set must be readable.</summary>
    [HttpGet("runs/findings")]
    public async Task<ActionResult<AnalysisRunFindingsPageDto>> GetRunFindings(
        [FromQuery] AnalysisRunFindingsRequest request,
        CancellationToken ct)
    {
        foreach (var runId in request.RunIds.Distinct())
        {
            var pipelineId = await pipelineRunService.GetPipelineIdForRunAsync(runId, ct);
            if (pipelineId is null) return NotFound();
            if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct))
                return Forbid();
        }
        return Ok(await runResults.GetRunFindingsAsync(request, ct));
    }

    [HttpGet("projects/{projectId:int}/findings")]
    public async Task<ActionResult<PaginatedResult<AnalysisFindingDto>>> GetFindings(
        int projectId,
        [FromQuery] AnalysisFindingPaginationRequest request,
        CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetFindingsAsync(projectId, request, ct));
    }

    [HttpGet("portfolio")]
    public async Task<ActionResult<PaginatedResult<AnalysisPortfolioRowDto>>> GetPortfolio(
        [FromQuery] AnalysisPortfolioPaginationRequest request,
        CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(
            User, ResourceType.Project, Permission.Read, ct);
        return Ok(await service.GetPortfolioAsync(accessibleIds, request, ct));
    }

    /// <summary>The organizations, projects and scanners the portfolio's column filters
    /// offer.</summary>
    [HttpGet("portfolio/filter-values")]
    public async Task<ActionResult<AnalysisPortfolioFilterValuesDto>> GetPortfolioFilterValues(CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(
            User, ResourceType.Project, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new AnalysisPortfolioFilterValuesDto());
        return Ok(await service.GetPortfolioFilterValuesAsync(accessibleIds, ct));
    }

    [HttpGet("portfolio/projects")]
    public async Task<ActionResult<List<AnalysisPortfolioProjectDto>>> GetPortfolioProjects(
        CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(
            User, ResourceType.Project, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new List<AnalysisPortfolioProjectDto>());
        return Ok(await service.GetPortfolioProjectsAsync(accessibleIds, ct));
    }

    [HttpGet("findings/{findingId:int}")]
    public async Task<ActionResult<AnalysisFindingDto>> GetFinding(int findingId, CancellationToken ct)
    {
        var projectId = await service.GetFindingProjectIdAsync(findingId, ct);
        if (!projectId.HasValue) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Read, ct))
            return Forbid();
        var finding = await service.GetFindingAsync(findingId, ct);
        return finding is null ? NotFound() : Ok(finding);
    }

    [HttpGet("findings/{findingId:int}/occurrences")]
    public async Task<ActionResult<List<AnalysisFindingOccurrenceDto>>> GetFindingOccurrences(
        int findingId,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var projectId = await service.GetFindingProjectIdAsync(findingId, ct);
        if (!projectId.HasValue) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetFindingOccurrencesAsync(findingId, take, ct));
    }

    [HttpGet("projects/{projectId:int}/reports")]
    public async Task<ActionResult<PaginatedResult<AnalysisReportDto>>> GetReports(
        int projectId,
        [FromQuery] PaginationRequest request,
        CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetReportsAsync(projectId, request, ct));
    }

    [HttpGet("projects/{projectId:int}/summary")]
    public async Task<ActionResult<AnalysisProjectSummaryDto>> GetProjectSummary(int projectId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetProjectSummaryAsync(projectId, ct));
    }

    [HttpGet("projects/{projectId:int}/metrics")]
    public async Task<ActionResult<PaginatedResult<AnalysisMetricDto>>> GetMetrics(
        int projectId,
        [FromQuery] AnalysisMetricPaginationRequest request,
        CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetMetricsAsync(projectId, request, ct));
    }

    [HttpGet("projects/{projectId:int}/components")]
    public async Task<ActionResult<PaginatedResult<AnalysisComponentDto>>> GetComponents(
        int projectId,
        [FromQuery] AnalysisComponentPaginationRequest request,
        CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetComponentsAsync(projectId, request, ct));
    }

    [HttpGet("projects/{projectId:int}/tracking")]
    public async Task<ActionResult<AnalysisTrackingStatusDto>> GetTrackingStatus(int projectId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct)) return Forbid();
        var status = await service.GetTrackingStatusAsync(projectId, ct);
        return status is null ? NoContent() : Ok(status);
    }

    [HttpGet("projects/{projectId:int}/vulnerabilities")]
    public async Task<ActionResult<PaginatedResult<AnalysisVulnerabilityObservationDto>>> GetVulnerabilities(
        int projectId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct)) return Forbid();
        return Ok(await service.GetVulnerabilityObservationsAsync(projectId, request, ct));
    }

    [HttpGet("projects/{projectId:int}/policies")]
    public async Task<ActionResult<List<AnalysisPolicyDto>>> GetPolicies(int projectId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetPoliciesAsync(projectId, ct));
    }

    [HttpPost("projects/{projectId:int}/policies")]
    public async Task<ActionResult<AnalysisPolicyDto>> CreatePolicy(
        int projectId,
        [FromBody] UpsertAnalysisPolicyRequest request,
        CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Admin, ct))
            return Forbid();
        var policy = await service.CreatePolicyAsync(projectId, request, ct);
        return CreatedAtAction(nameof(GetPolicies), new { projectId }, policy);
    }

    [HttpPut("projects/{projectId:int}/policies/{policyId:int}")]
    public async Task<ActionResult<AnalysisPolicyDto>> UpdatePolicy(
        int projectId,
        int policyId,
        [FromBody] UpsertAnalysisPolicyRequest request,
        CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Admin, ct))
            return Forbid();
        return Ok(await service.UpdatePolicyAsync(projectId, policyId, request, ct));
    }

    [HttpPost("projects/{projectId:int}/policies/batch")]
    public async Task<ActionResult<List<AnalysisPolicyDto>>> ApplyPolicyBatch(
        int projectId,
        [FromBody] ApplyAnalysisPolicyBatchRequest request,
        CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Admin, ct))
            return Forbid();
        return Ok(await service.ApplyPolicyBatchAsync(null, projectId, request, ct));
    }

    [HttpPost("projects/{projectId:int}/policies/preview")]
    public async Task<ActionResult<AnalysisPolicySetPreviewDto>> PreviewPolicies(
        int projectId,
        [FromBody] PreviewAnalysisPolicySetRequest request,
        CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();
        return Ok(await service.PreviewPolicySetAsync(null, projectId, request, ct));
    }

    [HttpGet("projects/{projectId:int}/policies/{policyId:int}/revisions")]
    public async Task<ActionResult<List<AnalysisPolicyRevisionDto>>> GetPolicyRevisions(
        int projectId,
        int policyId,
        CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetPolicyRevisionsAsync(null, projectId, policyId, ct));
    }

    [HttpPost("projects/{projectId:int}/policies/{policyId:int}/rollback/{version:int}")]
    public async Task<ActionResult<AnalysisPolicyDto>> RollbackPolicy(
        int projectId,
        int policyId,
        int version,
        CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Admin, ct))
            return Forbid();
        return Ok(await service.RollbackPolicyAsync(null, projectId, policyId, version, ct));
    }

    [HttpGet("projects/{projectId:int}/exceptions")]
    public async Task<ActionResult<List<AnalysisPolicyExceptionDto>>> GetExceptions(int projectId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetExceptionsAsync(projectId, ct));
    }

    [HttpPost("projects/{projectId:int}/exceptions")]
    public async Task<ActionResult<AnalysisPolicyExceptionDto>> CreateException(
        int projectId,
        [FromBody] CreateAnalysisPolicyExceptionRequest request,
        CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Admin, ct))
            return Forbid();
        var exception = await service.CreateExceptionAsync(projectId, request, Actor(), ct);
        return CreatedAtAction(nameof(GetExceptions), new { projectId }, exception);
    }

    [HttpDelete("projects/{projectId:int}/exceptions/{exceptionId:int}")]
    public async Task<IActionResult> RevokeException(int projectId, int exceptionId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Admin, ct))
            return Forbid();
        await service.RevokeExceptionAsync(projectId, exceptionId, Actor(), ct);
        return NoContent();
    }

    [HttpGet("findings/{findingId:int}/decisions")]
    public async Task<ActionResult<List<AnalysisFindingDecisionDto>>> GetFindingDecisions(int findingId, CancellationToken ct)
    {
        var projectId = await service.GetFindingProjectIdAsync(findingId, ct);
        if (!projectId.HasValue) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Read, ct))
            return Forbid();
        return Ok(await decisions.GetFindingDecisionsAsync(findingId, ct));
    }

    [HttpPost("findings/{findingId:int}/decisions")]
    public async Task<ActionResult<AnalysisFindingDecisionDto>> CreateFindingDecision(
        int findingId,
        [FromBody] CreateAnalysisFindingDecisionRequest request,
        CancellationToken ct)
    {
        var projectId = await service.GetFindingProjectIdAsync(findingId, ct);
        if (!projectId.HasValue) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Admin, ct))
            return Forbid();
        var decision = await decisions.CreateFindingDecisionAsync(findingId, request, Actor(), ct);
        return CreatedAtAction(nameof(GetFindingDecisions), new { findingId }, decision);
    }

    /// <summary>Reverts the active decision of the finding (kept in its history, audited); the
    /// finding is open again. The same permission as deciding.</summary>
    [HttpDelete("findings/{findingId:int}/decisions/active")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RevokeFindingDecision(int findingId, CancellationToken ct)
    {
        var projectId = await service.GetFindingProjectIdAsync(findingId, ct);
        if (!projectId.HasValue) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Admin, ct))
            return Forbid();
        await decisions.RevokeActiveFindingDecisionAsync(findingId, Actor(), ct);
        return NoContent();
    }

    private string Actor() => User.Identity?.Name ?? "unknown";
}
