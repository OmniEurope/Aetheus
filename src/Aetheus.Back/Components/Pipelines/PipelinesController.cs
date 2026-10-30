// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.RateLimiting;

namespace Aetheus.Back.Components.Pipelines;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
public class PipelinesController(
    IPipelineService pipelineService,
    IPipelineRunService runService,
    IPipelineApprovalService approvalService,
    IPipelineArtifactService artifactService,
    IPipelineWebhookService webhookService,
    IPipelineOwnerAuthorization ownerAuthz,
    IResourceAuthorizationService authz,
    IPipelineRunToEndPolicy runToEnd) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<PipelineDto>>> GetPipelines(
        [FromQuery] PipelinePaginationRequest request, CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Pipeline, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new PaginatedResult<PipelineDto>());
        return Ok(await pipelineService.GetPipelinesAsync(request, accessibleIds, ct));
    }

    [HttpGet("dependencies")]
    public async Task<ActionResult<PipelineDependencyGroupsDto>> GetDependencies(
        CancellationToken ct, [FromQuery] int? serverId = null, [FromQuery] int? projectId = null)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Pipeline, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new PipelineDependencyGroupsDto());
        return Ok(await pipelineService.GetDependencyGroupsAsync(accessibleIds, serverId, projectId, ct));
    }

    [HttpGet("runs/active")]
    public async Task<ActionResult<List<PipelineRunDto>>> GetActiveRuns([FromQuery] int? projectId, CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Pipeline, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new List<PipelineRunDto>());
        return Ok(await runService.GetActiveRunsAsync(accessibleIds, projectId, ct));
    }

    [HttpGet("runs/recent")]
    public async Task<ActionResult<List<PipelineRunDto>>> GetRecentRuns(
        [FromQuery] int? projectId, CancellationToken ct, [FromQuery] int? serverId = null)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Pipeline, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new List<PipelineRunDto>());
        return Ok(await runService.GetRecentRunsAsync(accessibleIds, projectId, serverId, ct));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PipelineDto>> GetPipeline(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, id, Permission.Read, ct))
            return Forbid();

        var pipeline = await pipelineService.GetPipelineAsync(id, ct);
        if (pipeline is null) return NotFound();
        return Ok(pipeline);
    }

    [HttpGet("{id:int}/source")]
    public async Task<ActionResult<PipelineSourceDto>> GetSource(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, id, Permission.Read, ct))
            return Forbid();

        // Four columns of the pipeline, not the pipeline with its YAML and runs.
        var (found, source) = await pipelineService.GetPipelineSourceByIdAsync(id, ct);
        if (!found) return NotFound();
        return source is null ? NoContent() : Ok(source);
    }

    [HttpGet("{id:int}/repository-definition")]
    public async Task<ActionResult<PipelineRepositoryDefinitionDto>> GetRepositoryDefinition(
        int id, [FromQuery] string? sourceBranch, [FromQuery] string? name, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, id, Permission.Write, ct))
            return Forbid();

        var definition = await pipelineService.GetRepositoryDefinitionAsync(id, sourceBranch, name, ct);
        return definition is null ? NotFound() : Ok(definition);
    }

    [HttpPost]
    public async Task<ActionResult<PipelineDto>> CreatePipeline([FromBody] CreatePipelineRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, null, Permission.Write, ct))
            return Forbid();
        // ADR-059: a pipeline goes to a project whose organization lets the caller write pipelines; one
        // without a project belongs to no organization and is a site Admin's only.
        if (!await authz.CanCreateInProjectAsync(User, ResourceType.Pipeline, request.ProjectId, ct))
            return Forbid();
        if (!await ownerAuthz.CanOwnAsync(User, request.ProjectId, request.EnvironmentId, request.ProjectServerId, ct))
            return Forbid();

        // Reject invalid YAML at save time with the concrete errors (same contract as
        // TriggerRun/Preflight) so the editor can surface them instead of failing generically.
        var validation = await pipelineService.ValidateYamlStrictAsync(
            request.YamlDefinition, request.ProjectId, request.EnvironmentId, request.ProjectServerId, ct: ct)
            ?? pipelineService.ValidateYamlStrict(request.YamlDefinition);
        if (!validation.IsValid) return BadRequest(validation);

        var pipeline = await pipelineService.CreatePipelineAsync(request, ct);
        return CreatedAtAction(nameof(GetPipeline), new { id = pipeline.Id }, pipeline);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<PipelineDto>> UpdatePipeline(int id, [FromBody] UpdatePipelineRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, id, Permission.Write, ct))
            return Forbid();

        var existing = await pipelineService.GetPipelineAsync(id, ct);
        if (existing is null) return NotFound();
        if (!await authz.CanCreateInProjectAsync(User, ResourceType.Pipeline, request.ProjectId, ct))
            return Forbid();

        // Both owners: the one left behind and the one moved to. Checking only the incoming one would let
        // a pipeline be walked out of an environment the caller cannot touch, only the existing one into.
        if (!await ownerAuthz.CanOwnAsync(User, existing.ProjectId, existing.EnvironmentId, existing.ProjectServerId, ct)
            || !await ownerAuthz.CanOwnAsync(User, request.ProjectId, request.EnvironmentId, request.ProjectServerId, ct))
            return Forbid();

        var validation = await pipelineService.ValidateYamlStrictAsync(
            request.YamlDefinition, request.ProjectId, request.EnvironmentId, request.ProjectServerId, ct: ct)
            ?? pipelineService.ValidateYamlStrict(request.YamlDefinition);
        if (!validation.IsValid) return BadRequest(validation);

        var pipeline = await pipelineService.UpdatePipelineAsync(id, request, ct);
        if (pipeline is null) return NotFound();
        return Ok(pipeline);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeletePipeline(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, id, Permission.Admin, ct))
            return Forbid();

        var deleted = await pipelineService.DeletePipelineAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    [HttpPost("{id:int}/run")]
    public async Task<ActionResult<PipelineRunDto>> TriggerRun(int id, [FromBody] PipelineRunRequest? request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.PipelineRun, id, ct))
            return Forbid();

        var pipeline = await pipelineService.GetPipelineAsync(id, ct);
        if (pipeline is null) return NotFound();
        if (await LaunchOptionsRefusalAsync(request, ct) is { } refusal) return refusal;
        var runToEndRequested = request?.RunToEnd == true;

        // Resolve Git exactly once. Authorization below and execution use the same branch, commit and YAML.
        var preparation = await runService.PrepareRunAsync(id, request?.SourceBranch, ct);
        // A YAML that cannot be read does not come back here: preparation throws a BadRequestException
        // whose message names the parser's line and column. Null means the pipeline is gone.
        if (preparation is null)
            return BadRequest("The pipeline was removed while the run was being prepared, so it cannot be run.");
        var runtimePreparation = await runService.ResolveRunParametersAsync(
            preparation, request?.Parameters, ct);
        if (!await CallerAdministersAllTargetsAsync(runtimePreparation.TargetServerIds, ct))
            return Forbid();
        // R-72: deploying despite the quality gate also takes Write on the project, on top of the rights above.
        var gateOverride = request?.GateOverride == true;
        if (gateOverride && !await CallerMayOverrideTheGateAsync(runtimePreparation.EffectiveProjectId, ct))
            return Forbid();

        // Invalid queue-time parameters surface as a BadRequestException and become HTTP 400.
        var run = await runService.TriggerPreparedRunAsync(
            runtimePreparation with
            {
                RunToEnd = runToEndRequested,
                GateOverrideBy = gateOverride ? User.Identity?.Name ?? "unknown" : null
            }, parameters: request?.Parameters, ct: ct,
            idempotencyKey: request?.IdempotencyKey);
        if (run is null) return NotFound();
        return CreatedAtAction(nameof(GetRun), new { runId = run.Id }, run);
    }

    /// <summary>R-72: Write on the pipeline's project. A pipeline with no project has no release to deploy
    /// despite the gate, so no one may ask it.</summary>
    private async Task<bool> CallerMayOverrideTheGateAsync(int? projectId, CancellationToken ct) =>
        projectId is { } id && await authz.HasPermissionAsync(User, ResourceType.Project, id, Permission.Write, ct);

    /// <summary>The refusal of a launch's options, or null: an invalid source branch, or run-to-end
    /// where this instance does not allow it.</summary>
    private async Task<ActionResult?> LaunchOptionsRefusalAsync(PipelineRunRequest? request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request?.SourceBranch)
            && !PipelineBranchValidator.IsValid(request.SourceBranch))
            return BadRequest("The selected source branch is invalid.");
        // Refused here, not only hidden in the UI: on a production instance the mode would let a
        // deployment run after failed tests.
        if (request?.RunToEnd == true && !await runToEnd.IsAllowedAsync(ct))
            return BadRequest(new ApiError { Message = PipelineRunToEndPolicy.RefusalMessage });
        return null;
    }

    /// <summary>The launch options this instance offers for a manual run, so the run dialog shows only
    /// what the backend will accept.</summary>
    [HttpGet("run-options")]
    public async Task<ActionResult<PipelineRunOptionsDto>> GetRunOptions(CancellationToken ct) =>
        Ok(new PipelineRunOptionsDto { RunToEndAllowed = await runToEnd.IsAllowedAsync(ct) });

    [HttpGet("dependencies/page")]
    public async Task<ActionResult<PaginatedResult<PipelineDependencyDto>>> GetDependencyPage(
        [FromQuery] PipelinePaginationRequest request, CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Pipeline, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new PaginatedResult<PipelineDependencyDto>());
        return Ok(await pipelineService.GetDependencyPageAsync(request, accessibleIds, ct));
    }

    /// <summary>P: the queue-time parameters declared by the pipeline's authoritative (git-first) YAML,
    /// so the UI can render the run dialog. Empty when the pipeline declares no <c>parameters:</c>.</summary>
    [HttpGet("{id:int}/parameters")]
    public async Task<ActionResult<List<PipelineRunParameterDto>>> GetRunParameters(int id, [FromQuery] string? sourceBranch, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.PipelineRun, id, ct))
            return Forbid();

        if (!string.IsNullOrWhiteSpace(sourceBranch) && !PipelineBranchValidator.IsValid(sourceBranch))
            return BadRequest("The selected source branch is invalid.");

        return await runService.GetRunParametersAsync(id, sourceBranch, ct);
    }

    /// <summary>Pre-flight: resolves each stage's target server without launching, so the UI can
    /// warn before a run starts when no online agent matches (the YAML 400 doesn't cover that).</summary>
    [HttpPost("{id:int}/preflight")]
    public async Task<ActionResult<PipelinePreflightDto>> Preflight(
        int id, CancellationToken ct, [FromQuery] string? sourceBranch = null,
        [FromBody] PipelineRunRequest? request = null)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.PipelineRun, id, ct))
            return Forbid();

        var pipeline = await pipelineService.GetPipelineAsync(id, ct);
        if (pipeline is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(sourceBranch) && !PipelineBranchValidator.IsValid(sourceBranch))
            return BadRequest("The selected source branch is invalid.");

        var preparation = await runService.PrepareRunAsync(id, sourceBranch, ct);
        if (preparation is null)
            return BadRequest("The pipeline was removed while the check was being prepared, so it cannot be checked.");

        // F-EXEC-1: Preflight enumerates the resolved target servers - same Server.Admin gate
        // as TriggerRun so it cannot be used as a recon oracle for servers the caller may not
        // deploy to.
        if (!await CallerAdministersAllTargetsAsync(preparation.TargetServerIds, ct))
            return Forbid();

        // The values the user chose in the launch dialog, when it has been answered: the preview then
        // resolves the stage targets a parameter names (deployServer) exactly as the launch will.
        var parameters = request?.Parameters is { Count: > 0 } supplied
            ? new Dictionary<string, string>(supplied, StringComparer.OrdinalIgnoreCase)
            : null;
        var result = await runService.PreflightAsync(preparation, parameters, ct);
        if (result is null) return NotFound();
        return Ok(result);
    }

    [HttpGet("{id:int}/runs")]
    public async Task<ActionResult<PaginatedResult<PipelineRunDto>>> GetRuns(int id, [FromQuery] PipelineRunPaginationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, id, Permission.Read, ct))
            return Forbid();

        return Ok(await runService.GetRunsAsync(id, request, ct));
    }

    [HttpGet("runs/{runId:int}")]
    public async Task<ActionResult<PipelineRunDto>> GetRun(int runId, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct))
            return Forbid();

        var run = await runService.GetRunAsync(runId, ct);
        if (run is null) return NotFound();
        return Ok(run);
    }

    [HttpGet("runs/{runId:int}/queue")]
    public async Task<ActionResult<PipelineRunQueueStateDto>> GetRunQueue(int runId, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct)) return Forbid();
        return Ok(await runService.GetRunQueueStateAsync(runId, ct));
    }
    [HttpPost("validate")]
    [RequestSizeLimit(256 * 1024)]
    public async Task<ActionResult<YamlValidationResultDto>> ValidateYaml(
        [FromBody] ValidateYamlRequest request, CancellationToken ct)
    {
        int? organizationId = request.OrganizationId;
        if (!User.IsInRole("Admin"))
        {
            // ADR-059: membership alone grants nothing; a role must let the caller read pipelines there.
            var organizationIds = await authz.GetOrganizationIdsWithPermissionAsync(
                User, ResourceType.Pipeline, Permission.Read, ct);
            organizationId ??= organizationIds.FirstOrDefault();
            if (organizationId <= 0 || !organizationIds.Contains(organizationId.Value))
                return Forbid();
        }
        var result = await pipelineService.ValidateYamlStrictAsync(
            request.Yaml, null, null, null, organizationId, ct)
            ?? pipelineService.ValidateYamlStrict(request.Yaml);
        if (!result.IsValid) return BadRequest(result);
        return Ok(result);
    }
    [HttpPost("{id:int}/dry-run")]
    public async Task<ActionResult<DryRunResultDto>> DryRun(
        int id,
        [FromBody, BoundedDictionary(64, 200, 4000)] Dictionary<string, string>? additionalVars,
        CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, id, Permission.Write, ct))
            return Forbid();

        // F-EXEC-1: dry-run reveals the fully resolved commands (with masked secrets) per target
        // server - gate it on Server.Admin like the real run so it isn't a recon oracle.
        if (!await CallerAdministersAllTargetsAsync(id, ct))
            return Forbid();

        var result = await runService.DryRunAsync(id, additionalVars, ct);
        if (result is null) return NotFound();
        return Ok(result);
    }
    [HttpPost("runs/{runId:int}/cancel")]
    public async Task<IActionResult> CancelRun(int runId, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, PermissionKeys.PipelineRun, pipelineId.Value, ct))
            return Forbid();

        var cancelled = await runService.CancelRunAsync(runId, PipelineRunCancellation.ByUser(User.Identity?.Name), ct);
        if (!cancelled) return NotFound();
        return NoContent();
    }
    /// <summary>R-39: resumes a run waiting after a missed confirmation. Requires pipeline.run (R-11) on the run's
    /// pipeline and, like a retry, Server.Admin on every target server of the run's captured definition. A run
    /// that is not waiting, or whose window elapsed, answers 409 with the reason.</summary>
    [HttpPost("runs/{runId:int}/resume")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResumeRun(int runId, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, PermissionKeys.PipelineRun, pipelineId.Value, ct))
            return Forbid();

        // A resume replays the run's captured steps on its targets, like a retry (BACK-020): authorize the
        // run's captured definition with the same Server.Admin gate.
        var resumeTargets = await runService.GetRetryTargetServerIdsAsync(runId, ct);
        if (resumeTargets is null) return NotFound();
        if (!await CallerAdministersAllTargetsAsync(resumeTargets, ct))
            return Forbid();

        await runService.ResumeAwaitingRunAsync(runId, User.Identity?.Name, ct);
        return NoContent();
    }
    /// <summary>Re-runs failed or cancelled steps of a terminal run (instead of a brand-new run).</summary>
    [HttpPost("runs/{runId:int}/retry-failed")]
    public async Task<ActionResult<PipelineRunDto>> RetryFailedSteps(int runId, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, PermissionKeys.PipelineRun, pipelineId.Value, ct))
            return Forbid();

        // BACK-020: a retry replays the run's captured definition, not the current one: authorize that.
        var retryTargets = await runService.GetRetryTargetServerIdsAsync(runId, ct);
        if (retryTargets is null) return NotFound();
        if (!await CallerAdministersAllTargetsAsync(retryTargets, ct))
            return Forbid();

        var run = await runService.RetryFailedStepsAsync(runId, ct);
        if (run is null) return NotFound();
        return Ok(run);
    }
    /// <summary>G: re-launches a run - the current definition (a fresh run), or the source run's
    /// captured YAML snapshot pinned to its commit / floated to the branch head.</summary>
    [HttpPost("runs/{runId:int}/rerun")]
    public async Task<ActionResult<PipelineRunDto>> Rerun(int runId, [FromQuery] RerunMode mode, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, PermissionKeys.PipelineRun, pipelineId.Value, ct))
            return Forbid();

        // Same Server.Admin gate as TriggerRun - a rerun launches free-form shell (= RCE). BACK-020: on the
        // snapshot, commit and parameters the rerun will execute, and the launch starts from that plan.
        var plan = await runService.PrepareRerunAsync(runId, mode, ct);
        if (plan is null) return NotFound();
        if (!await CallerAdministersAllTargetsAsync(plan.Preparation.TargetServerIds, ct))
            return Forbid();

        var run = await runService.TriggerPreparedRunAsync(
            plan.Preparation, plan.AdditionalVariables, plan.Parameters, ct);
        if (run is null) return NotFound();
        return CreatedAtAction(nameof(GetRun), new { runId = run.Id }, run);
    }

    [HttpGet("runs/{runId:int}/artifacts")]
    public async Task<ActionResult<List<PipelineArtifactDto>>> GetArtifacts(int runId, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct))
            return Forbid();

        return Ok(await artifactService.GetArtifactsAsync(runId, ct));
    }

    [HttpPost("runs/{runId:int}/artifacts")]
    [Authorize(Policy = "AgentToken")]
    public async Task<ActionResult<PipelineArtifactDto>> PublishArtifact(
        int runId, [FromBody] PublishArtifactRequest request, CancellationToken ct)
    {
        // F-06: agent IDOR \u2014 ensure the caller's server is actually assigned to this run.
        var serverIdClaim = User.FindFirst("ServerId")?.Value;
        if (!int.TryParse(serverIdClaim, out var agentServerId)) return Forbid();
        if (!await runService.IsServerAssignedToRunAsync(runId, agentServerId, ct)) return Forbid();

        var artifact = await artifactService.PublishArtifactAsync(runId, request, ct);
        if (artifact is null) return NotFound();
        return Ok(artifact);
    }

    [HttpGet("runs/{runId:int}/approvals")]
    public async Task<ActionResult<List<PipelineApprovalDto>>> GetApprovals(int runId, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct))
            return Forbid();

        return Ok(await approvalService.GetApprovalsAsync(runId, ct));
    }

    [HttpPost("approvals/{approvalId:int}/decide")]
    public async Task<ActionResult<PipelineApprovalDto>> DecideApproval(
        int approvalId, [FromBody] ApprovalDecisionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // TimedOut is issued by the expiry sweep only; a user approves or rejects (RBAC audit, finding 11).
        if (request.Decision is not (ApprovalStatus.Approved or ApprovalStatus.Rejected))
            return BadRequest(new ApiError { Message = $"'{request.Decision}' is not a decision; an approval is approved or rejected." });
        var scope = await approvalService.GetApprovalScopeAsync(approvalId, ct);
        if (scope is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, PermissionKeys.PipelineApprovalDecide, scope.PipelineId, ct)
            || !await MayApproveAsync(scope, ct))
            return Forbid();

        // A refused decision answers with its reason (ErrorHandlingMiddleware), never a bare 404.
        return Ok(await approvalService.DecideApprovalAsync(approvalId, request, ct));
    }

    // RBAC audit, finding 11: starting a run cannot also be what lets it through its gate, so deciding is
    // pipeline.approval.decide (R-11) on top of this: the approver administers the gated project, or the environment the
    // approval was asked for; a pipeline of neither needs Admin on the pipeline itself. Approving one's
    // own run stays allowed (a decision of the product, not a gap).
    private async Task<bool> MayApproveAsync(PipelineApprovalScope scope, CancellationToken ct)
    {
        if (scope.ProjectId is { } projectId)
        {
            if (await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Admin, ct))
                return true;
        }
        if (scope.EnvironmentId is { } environmentId)
        {
            if (await authz.HasPermissionAsync(User, ResourceType.Environment, environmentId, Permission.Admin, ct))
                return true;
        }
        if (scope.ProjectId is not null || scope.EnvironmentId is not null) return false;
        return await authz.HasPermissionAsync(User, ResourceType.Pipeline, scope.PipelineId, Permission.Admin, ct);
    }

    [AllowAnonymous]
    [EnableRateLimiting("webhook")]
    [RequestSizeLimit(64 * 1024)]
    [HttpPost("webhook")]
    public async Task<IActionResult> HandleWebhook(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(ct);

        var signature = Request.Headers["X-Hub-Signature-256"].FirstOrDefault()
                        ?? Request.Headers["X-Gitlab-Token"].FirstOrDefault();

        var result = await webhookService.HandleWebhookAsync(rawBody, signature, ct);
        if (!result) return Unauthorized(new ApiError { Message = "Invalid webhook signature." });
        return NoContent();
    }

    [HttpGet("runs/{runId:int}/test-results")]
    public async Task<ActionResult<List<PipelineTestResultDto>>> GetTestResults(int runId, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct))
            return Forbid();

        return Ok(await artifactService.GetTestResultsAsync(runId, ct));
    }

    [HttpPost("runs/{runId:int}/test-results")]
    [Authorize(Policy = "AgentToken")]
    public async Task<ActionResult<PipelineTestResultSummaryDto>> PublishTestResults(
        int runId, [FromBody] PublishTestResultsRequest request, CancellationToken ct)
    {
        // F-06: agent IDOR.
        var serverIdClaim = User.FindFirst("ServerId")?.Value;
        if (!int.TryParse(serverIdClaim, out var agentServerId)) return Forbid();
        if (!await runService.IsServerAssignedToRunAsync(runId, agentServerId, ct)) return Forbid();

        var summary = await artifactService.PublishTestResultsAsync(runId, request, ct);
        if (summary is null) return NotFound();
        return Ok(summary);
    }

    [HttpGet("runs/{runId:int}/coverage")]
    public async Task<ActionResult<PipelineCoverageSummaryDto>> GetCoverage(int runId, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct))
            return Forbid();

        var summary = await artifactService.GetCoverageSummaryAsync(runId, ct);
        if (summary is null) return NotFound();
        return Ok(summary);
    }

    [HttpGet("runs/{runId:int}/coverage/assemblies")]
    public async Task<ActionResult<PaginatedResult<CoverageAssemblyDto>>> GetCoverageAssemblies(
        int runId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct))
            return Forbid();

        return Ok(await artifactService.GetCoverageAssembliesAsync(runId, request, ct));
    }

    [HttpGet("runs/{runId:int}/coverage-trend")]
    public async Task<ActionResult<List<CoverageTrendPointDto>>> GetCoverageTrend(int runId, [FromQuery] int take = 15, CancellationToken ct = default)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct))
            return Forbid();

        return Ok(await artifactService.GetCoverageTrendAsync(runId, take, ct));
    }

    [HttpGet("projects/{projectId:int}/quality-trend")]
    public async Task<ActionResult<ProjectQualityTrendDto>> GetProjectQualityTrend(int projectId, [FromQuery] int take = 15, CancellationToken ct = default)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();

        return Ok(await artifactService.GetProjectQualityTrendAsync(projectId, take, ct));
    }

    [HttpGet("runs/{runId:int}/metrics-trend")]
    public async Task<ActionResult<List<ComplexityTrendPointDto>>> GetMetricsTrend(int runId, [FromQuery] int take = 15, CancellationToken ct = default)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct))
            return Forbid();

        return Ok(await artifactService.GetComplexityTrendAsync(runId, take, ct));
    }

    [HttpPost("runs/{runId:int}/coverage")]
    [Authorize(Policy = "AgentToken")]
    // A full-solution Cobertura report (JSON-embedded, escaped) routinely exceeds the global 10 MB
    // Kestrel body cap, which reset the upload mid-stream and surfaced on the agent as "Error while
    // copying content to a stream". Raise the per-action cap so the coverage step can publish.
    [RequestSizeLimit(CoverageUploadLimits.MaxRawXmlBytes)]
    public async Task<ActionResult<PipelineCoverageSummaryDto>> PublishCoverage(
        int runId, [FromBody] PublishCoverageRequest request, CancellationToken ct)
    {
        return await PublishCoverageCore(runId, request, ct);
    }

    [HttpPost("runs/{runId:int}/coverage/raw")]
    [Authorize(Policy = "AgentToken")]
    [Consumes("application/xml")]
    [RequestSizeLimit(CoverageUploadLimits.MaxRawXmlBytes)]
    public async Task<ActionResult<PipelineCoverageSummaryDto>> PublishCoverageRaw(
        int runId, [FromQuery] string? stageName, [FromQuery] string? stepName, CancellationToken ct)
    {
        if (Request.ContentLength > CoverageUploadLimits.MaxRawXmlBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (stageName?.Length > 200 || stepName?.Length > 200)
            return BadRequest("Coverage stage and step names are limited to 200 characters.");

        using var reader = new StreamReader(Request.Body);
        var xmlContent = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(xmlContent))
            return BadRequest("Coverage XML is required.");
        return await PublishCoverageCore(runId, new PublishCoverageRequest
        {
            XmlContent = xmlContent,
            StageName = stageName,
            StepName = stepName
        }, ct);
    }

    [HttpGet("runs/{runId:int}/lint")]
    public async Task<ActionResult<PipelineLintSummaryDto>> GetLint(int runId, CancellationToken ct)
    {
        var pipelineId = await runService.GetPipelineIdForRunAsync(runId, ct);
        if (pipelineId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId.Value, Permission.Read, ct))
            return Forbid();

        var summary = await artifactService.GetLintSummaryAsync(runId, ct);
        if (summary is null) return NotFound();
        return Ok(summary);
    }

    [HttpPost("runs/{runId:int}/lint")]
    [Authorize(Policy = "AgentToken")]
    // A large SARIF report can also breach the global 10 MB cap - same override as coverage above.
    [RequestSizeLimit(104_857_600)]
    public async Task<ActionResult<PipelineLintSummaryDto>> PublishLint(
        int runId, [FromBody] PublishLintRequest request, CancellationToken ct)
    {
        var serverIdClaim = User.FindFirst("ServerId")?.Value;
        if (!int.TryParse(serverIdClaim, out var agentServerId)) return Forbid();
        if (!await runService.IsServerAssignedToRunAsync(runId, agentServerId, ct)) return Forbid();

        var summary = await artifactService.PublishLintAsync(runId, request, ct);
        if (summary is null) return NotFound();
        return Ok(summary);
    }

    [HttpPost("runs/{runId:int}/complexity")]
    [Authorize(Policy = "AgentToken")]
    public async Task<IActionResult> PublishComplexity(
        int runId, [FromBody] PublishComplexityRequest request, CancellationToken ct)
    {
        var serverIdClaim = User.FindFirst("ServerId")?.Value;
        if (!int.TryParse(serverIdClaim, out var agentServerId)) return Forbid();
        if (!await runService.IsServerAssignedToRunAsync(runId, agentServerId, ct)) return Forbid();

        return await artifactService.PublishComplexityAsync(runId, request, ct) ? Ok() : NotFound();
    }

    private async Task<ActionResult<PipelineCoverageSummaryDto>> PublishCoverageCore(
        int runId, PublishCoverageRequest request, CancellationToken ct)
    {
        var serverIdClaim = User.FindFirst("ServerId")?.Value;
        if (!int.TryParse(serverIdClaim, out var agentServerId)) return Forbid();
        if (!await runService.IsServerAssignedToRunAsync(runId, agentServerId, ct)) return Forbid();

        var summary = await artifactService.PublishCoverageAsync(runId, request, ct);
        if (summary is null) return NotFound();
        return Ok(summary);
    }

    // F-EXEC-1: a pipeline step is a free-form shell command, i.e. arbitrary code execution on
    // the target server - exactly what the F-15 gate on POST /api/tasks requires Server Admin
    // for. Triggering a run is only gated by pipeline.run, so without this check a user with
    // the run permission but no Server Admin could escalate to RCE on servers they do not
    // administer. Require Server Admin on EVERY server any stage could resolve to (pool >
    // environment > agent, all statuses). An empty set means the run can target nothing and
    // will fail "no server matched" - nothing to authorize. Non-interactive triggers
    // (webhook / scheduler) have no ClaimsPrincipal and are tracked separately as F-EXEC-1b.
    private async Task<bool> CallerAdministersAllTargetsAsync(int pipelineId, CancellationToken ct)
    {
        var preparation = await runService.PrepareRunAsync(pipelineId, ct: ct);
        return preparation is not null
            && await CallerAdministersAllTargetsAsync(preparation.TargetServerIds, ct);
    }

    private async Task<bool> CallerAdministersAllTargetsAsync(
        IReadOnlyCollection<int> targetIds, CancellationToken ct)
    {
        foreach (var serverId in targetIds)
        {
            if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct))
                return false;
        }
        return true;
    }

}
