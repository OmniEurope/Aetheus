// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Microsoft.AspNetCore.RateLimiting;

namespace Aetheus.Back.Components.Releases;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ReleasesController(
    IReleaseService service,
    IResourceAuthorizationService authz,
    IConfiguration configuration,
    IPipelineRunService pipelineRunService,
    IReleaseDeploymentAnnouncer deploymentAnnouncer,
    IReleaseStatusRestoreService statusRestore) : ControllerBase
{
    /// <summary>
    /// Releases of every project a server is involved in.
    ///
    /// Relocated from ServersController with its route unchanged: the endpoint is published as
    /// <c>api/servers/{serverId}/releases</c> and the frontend calls it by that path, so moving the
    /// controller must not move the URL. The authorization is still the SERVER's Read permission -
    /// the resource being scoped is the server, whatever module now serves the request.
    ///
    /// A360-18: this was the only list endpoint of the module that returned an unbounded list, on a
    /// table built for long retention. It is paginated now, like every other list route. The response
    /// shape changed from List to PaginatedResult, and its single consumer (the front's ReleasesList in
    /// server scope) moved with it - there is no other caller, so no compatibility route is kept for a
    /// shape nobody else reads.
    /// </summary>
    [HttpGet("/api/servers/{serverId:int}/releases")]
    public async Task<ActionResult<PaginatedResult<ReleaseDto>>> GetServerReleases(
        int serverId, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();

        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Release, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new PaginatedResult<ReleaseDto>());
        return Ok(await service.GetServerReleasesAsync(serverId, request, ct, accessibleIds));
    }

    /// <summary>The project and pipeline names a server's releases list filters offer.</summary>
    [HttpGet("/api/servers/{serverId:int}/releases/filter-values")]
    public async Task<ActionResult<ReleaseFilterValuesDto>> GetServerReleaseFilterValues(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();

        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Release, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new ReleaseFilterValuesDto());
        return Ok(await service.GetReleaseFilterValuesAsync(null, accessibleIds, serverId, ct));
    }

    /// <summary>The project and pipeline names the releases list filters offer, across the releases
    /// the caller can read (of one project when <paramref name="projectId"/> is set).</summary>
    [HttpGet("filter-values")]
    public async Task<ActionResult<ReleaseFilterValuesDto>> GetReleaseFilterValues([FromQuery] int? projectId, CancellationToken ct)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Release, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new ReleaseFilterValuesDto());
        return Ok(await service.GetReleaseFilterValuesAsync(projectId, accessibleIds, null, ct));
    }

    [HttpGet]
    public async Task<ActionResult<PaginatedResult<ReleaseDto>>> GetReleases(
        [FromQuery] int? projectId, [FromQuery] PaginationRequest request, CancellationToken ct,
        [FromQuery] bool deployable = false)
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Release, Permission.Read, ct);
        if (accessibleIds is { Count: 0 }) return Ok(new PaginatedResult<ReleaseDto>());
        // deployable=true restricts results to releases whose payload can be restored.
        return Ok(await service.GetReleasesAsync(projectId, request, accessibleIds, ct, deployable));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ReleaseDto>> GetRelease(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Release, id, Permission.Read, ct))
            return Forbid();
        var release = await service.GetReleaseAsync(id, ct);
        if (release is null) return NotFound();
        return Ok(release);
    }

    [HttpGet("{id:int}/rollback-preview")]
    public async Task<ActionResult<ReleaseRollbackPreviewDto>> GetRollbackPreview(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Release, id, Permission.Read, ct))
            return Forbid();
        return Ok(await service.GetRollbackPreviewAsync(id, ct));
    }

    [HttpGet("by-run/{runId:int}")]
    public async Task<ActionResult<List<ReleaseDto>>> GetByRun(int runId, CancellationToken ct)
    {
        var releases = await service.GetReleasesByRunAsync(runId, ct);
        // Per-resource filtering, consistent with GetReleases/GetRelease: a run may reference
        // releases on projects the caller can't access - never leak those. null = full access.
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Release, Permission.Read, ct);
        if (accessibleIds is not null)
            releases = releases.Where(r => accessibleIds.Contains(r.Id)).ToList();
        return Ok(releases);
    }

    [HttpPost("sync/{projectId:int}")]
    public async Task<ActionResult<List<ReleaseDto>>> SyncReleases(int projectId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Write, ct))
            return Forbid();
        return Ok(await service.SyncReleasesAsync(projectId, ct));
    }

    [HttpPost("{id:int}/build")]
    public async Task<ActionResult<ReleaseDto>> TriggerBuild(int id, [FromBody] TriggerReleaseBuildRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Release, id, Permission.Write, ct))
            return Forbid();
        var (refusal, authorizedRun) = await AuthorizePipelineLaunchAsync(request.PipelineId, ct);
        if (refusal is not null) return refusal;
        var result = await service.TriggerReleaseBuildAsync(id, request, authorizedRun!, ct);
        return AcceptedAtAction(nameof(GetRelease), new { id = result.Id }, result);
    }

    /// <summary>Decision D2: runs a complete pipeline again from the commit of an archived release, the
    /// snapshot pinned to that commit (definition and workspace), authorized like any other launch.</summary>
    [HttpPost("{id:int}/relaunch")]
    public async Task<ActionResult<PipelineRunDto>> RelaunchFromCommit(int id, [FromBody] TriggerReleaseBuildRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Release, id, Permission.Write, ct))
            return Forbid();
        var release = await service.GetReleaseAsync(id, ct);
        if (release is null) return NotFound();
        if (!ReleaseRelaunch.CanRelaunch(release.Status, release.CommitHash))
            return BadRequest("Only an archived release with a full commit hash can be relaunched from its commit.");
        var branch = PipelineBranchValidator.IsValid(release.BranchName) ? release.BranchName : null;
        var (refusal, authorizedRun) = await AuthorizePipelineLaunchAsync(request.PipelineId, ct, branch, release.CommitHash);
        if (refusal is not null) return refusal;
        var run = await service.RelaunchFromCommitAsync(id, request, authorizedRun!, ct);
        return Accepted(run);
    }

    [HttpPost("{id:int}/rollback")]
    public async Task<ActionResult<ReleaseRollbackDto>> Rollback(int id, [FromBody] RollbackReleaseRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.ReleaseRollback, id, ct))
            return Forbid();
        var release = await service.GetReleaseAsync(id, ct);
        if (release is null) return NotFound();
        // A rollback can redeploy an application and, when explicitly requested, restore its data.
        // The rollback permission alone is therefore insufficient: the caller must also be allowed to operate
        // on the owning project.
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, release.ProjectId, Permission.Write, ct))
            return Forbid();
        var (refusal, authorizedRun) = await AuthorizePipelineLaunchAsync(request.PipelineId, ct);
        if (refusal is not null) return refusal;
        var result = await service.RollbackReleaseAsync(id, request, authorizedRun!, ct);
        return AcceptedAtAction(nameof(GetRelease), new { id }, result);
    }

    /// <summary>Recette R-13: puts a release whose recorded status is wrong back to a status its deployment
    /// history can end in (the release production still serves, left Failed, becomes Deployed again). A record
    /// correction over the project's deployment history: release.status.restore (R-11) and Project.Admin, reason audited.</summary>
    [HttpPost("{id:int}/status")]
    public async Task<ActionResult<ReleaseDto>> RestoreStatus(int id, [FromBody] RestoreReleaseStatusRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.ReleaseStatusRestore, id, ct))
            return Forbid();
        var release = await service.GetReleaseAsync(id, ct);
        if (release is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, release.ProjectId, Permission.Admin, ct))
            return Forbid();
        return Ok(await statusRestore.RestoreAsync(id, request, ct));
    }

    [HttpPost("{id:int}/promote")]
    public async Task<ActionResult<ReleaseDto>> Promote(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Release, id, Permission.Write, ct))
            return Forbid();
        var result = await service.PromoteReleaseAsync(id, ct);
        return AcceptedAtAction(nameof(GetRelease), new { id = result.Id }, result);
    }

    [HttpPost("create")]
    [Authorize(Policy = "AgentToken")]
    public async Task<ActionResult<ReleaseDto>> CreateRelease([FromBody] CreateReleaseRequest request, [FromQuery] int projectId, CancellationToken ct)
    {
        if (request.PipelineRunId is not > 0)
            return BadRequest(new ApiError { Message = "A valid pipeline run is required." });

        var serverIdClaim = User.FindFirst("ServerId")?.Value;
        if (!int.TryParse(serverIdClaim, out var agentServerId))
            return Forbid();

        var pipelineRunId = request.PipelineRunId.Value;
        if (!await pipelineRunService.IsServerAssignedToRunAsync(pipelineRunId, agentServerId, ct))
            return Forbid();

        var runContext = await pipelineRunService.GetRunPipelineContextAsync(pipelineRunId, ct);
        if (runContext?.ProjectId != projectId)
            return Forbid();
        // A "run to end" run went past a failure a normal run stops at: whatever it would publish
        // carries no proof, so it publishes nothing.
        if (await pipelineRunService.HasRunToEndFailureAsync(pipelineRunId, ct))
            return BadRequest(new ApiError
            {
                Message = "This run was launched in run-to-end mode and a step already failed: it cannot publish a release."
            });

        var result = await service.CreateReleaseFromPipelineAsync(
            projectId, pipelineRunId, request.Version, request.Changelog,
            request.CommitHash, request.TagName, request.BranchName, request.ArtifactPipelineRunId,
            request.Deployed, ct);
        // A release step that records its release as deployed is a deployment like any other.
        if (request.Deployed && result.Status == ReleaseStatus.Deployed)
            await deploymentAnnouncer.AnnounceAsync(result.Id, pipelineRunId, agentServerId, ct);
        return CreatedAtAction(nameof(GetRelease), new { id = result.Id }, result);
    }

    [HttpPost("webhook")]
    [AllowAnonymous]
    [EnableRateLimiting("webhook")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        var secret = configuration["Webhook:Secret"];
        // Without a secret the release webhook is not enabled on this instance: 404 with the
        // ApiError body (never 503, which reads as an outage), and nothing is read or processed.
        if (string.IsNullOrEmpty(secret))
            return NotFound(new ApiError { Message = "Release webhook is not enabled on this instance." });

        var signature = Request.Headers["X-Hub-Signature-256"].FirstOrDefault()
                     ?? Request.Headers["X-Gitlab-Token"].FirstOrDefault();

        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(ct);

        if (!service.ValidateWebhookSignature(signature, secret, rawBody))
            return Unauthorized();

        var payload = System.Text.Json.JsonSerializer.Deserialize<WebhookPayload>(rawBody);
        if (payload is null) return BadRequest();

        await service.HandleWebhookAsync(payload, ct);
        return Ok();
    }

    /// <summary>
    /// BACK-023: a release build or rollback launches a pipeline, so it needs what the direct pipeline
    /// route needs: pipeline.run, then Server.Admin on every server the prepared run can reach. The
    /// preparation authorized here is the one the service launches.
    /// </summary>
    private async Task<(ActionResult? Refusal, PipelineRunPreparation? Run)> AuthorizePipelineLaunchAsync(
        int pipelineId, CancellationToken ct, string? sourceBranch = null, string? commitHash = null)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.PipelineRun, pipelineId, ct))
            return (Forbid(), null);
        var preparation = commitHash is null
            ? await pipelineRunService.PrepareRunAsync(pipelineId, ct: ct)
            : await pipelineRunService.PrepareRunAtCommitAsync(pipelineId, sourceBranch, commitHash, ct);
        if (preparation is null) return (NotFound(), null);
        var authorizedRun = await pipelineRunService.ResolveRunParametersAsync(preparation, null, ct);
        foreach (var serverId in authorizedRun.TargetServerIds)
        {
            if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct))
                return (Forbid(), null);
        }
        return (null, authorizedRun);
    }
}
