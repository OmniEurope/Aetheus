// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.AiTasks;

[ApiController]
[Route("api/ai")]
[Authorize]
public sealed class AiTasksController(
    IAiTaskService service,
    IAiRunnerProfileService profiles,
    IResourceAuthorizationService authorization,
    // No IPipelineRunService: the one thing this controller needed from it was the pipeline id behind
    // a run, for an RBAC check. Injecting an orchestrator to read one integer put AiTasks inside the
    // module cycle, so the read became an own-read behind this module own service.
    Aetheus.Back.Components.Tasks.ITaskService taskService) : ControllerBase
{
    [HttpGet("profiles")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<PaginatedResult<AiRunnerProfileDto>>> GetProfiles(
        [FromQuery] PaginationRequest request, CancellationToken ct) =>
        Ok(await profiles.GetProfilesAsync(request, ct).ConfigureAwait(false));

    [HttpGet("profile-options")]
    public async Task<ActionResult<List<AiRunnerProfileDto>>> GetProfileOptions(CancellationToken ct)
    {
        var organizationIds = User.IsInRole("Admin")
            ? null
            : await authorization.GetUserOrganizationIdsAsync(User, ct).ConfigureAwait(false);
        if (organizationIds is null)
            return Ok((await profiles.GetProfilesAsync(
                new PaginationRequest { Page = 1, PageSize = 1000 }, ct).ConfigureAwait(false)).Items);
        return Ok(await profiles.GetProfileOptionsAsync(organizationIds, ct).ConfigureAwait(false));
    }

    [HttpGet("profiles/{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<AiRunnerProfileDto>> GetProfile(int id, CancellationToken ct)
    {
        var profile = await profiles.GetProfileAsync(id, ct).ConfigureAwait(false);
        return profile is null ? NotFound() : Ok(profile);
    }

    [HttpPost("profiles")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<AiRunnerProfileDto>> CreateProfile(
        [FromBody] CreateAiRunnerProfileRequest request, CancellationToken ct)
    {
        var profile = await profiles.CreateProfileAsync(request, ct).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetProfile), new { id = profile.Id }, profile);
    }

    [HttpPut("profiles/{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<AiRunnerProfileDto>> UpdateProfile(
        int id, [FromBody] UpdateAiRunnerProfileRequest request, CancellationToken ct)
    {
        var profile = await profiles.UpdateProfileAsync(id, request, ct).ConfigureAwait(false);
        return profile is null ? NotFound() : Ok(profile);
    }

    [HttpDelete("profiles/{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteProfile(int id, CancellationToken ct) =>
        await profiles.DeleteProfileAsync(id, ct).ConfigureAwait(false) ? NoContent() : NotFound();

    [HttpPost("profiles/test")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<AiConnectionTestDto>> TestProfile(
        [FromBody] TestAiRunnerProfileRequest request, CancellationToken ct) =>
        Ok(await profiles.TestConnectionAsync(request, ct).ConfigureAwait(false));

    /// <summary>Recette R3-7: reads the saved profile's model list at its provider now. 429 with
    /// <c>Retry-After</c> when it was read less than a minute ago.</summary>
    [HttpPost("profiles/{id:int}/model-list/refresh")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<AiRunnerProfileDto>> RefreshModelList(int id, CancellationToken ct)
    {
        var attempt = await profiles.RefreshModelListAsync(id, ct).ConfigureAwait(false);
        if (attempt is null) return NotFound();
        if (attempt.RetryAfter is not { } wait) return Ok(attempt.Profile);
        var seconds = (int)Math.Ceiling(wait.TotalSeconds);
        Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status429TooManyRequests, new ApiError
        {
            Message = $"The model list of this profile was read less than a minute ago. Try again in {seconds} s.",
            CorrelationId = HttpContext.TraceIdentifier
        });
    }

    [HttpGet("tasks")]
    public async Task<ActionResult<PaginatedResult<AiTaskDefinitionDto>>> GetDefinitions(
        [FromQuery] int? projectId, [FromQuery] int? serverId,
        [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        var projectIds = await authorization.GetAccessibleResourceIdsAsync(
            User, ResourceType.Project, Permission.Read, ct).ConfigureAwait(false);
        var serverIds = await authorization.GetAccessibleResourceIdsAsync(
            User, ResourceType.Server, Permission.Read, ct).ConfigureAwait(false);
        return Ok(await service.GetDefinitionsAsync(
            request, projectId, serverId, projectIds, serverIds, ct).ConfigureAwait(false));
    }

    [HttpGet("tasks/{id:int}")]
    public async Task<ActionResult<AiTaskDefinitionDto>> GetDefinition(int id, CancellationToken ct)
    {
        var definition = await service.GetDefinitionAsync(id, ct).ConfigureAwait(false);
        if (definition is null) return NotFound();
        return await HasOwnerPermissionAsync(
            definition.ProjectId, definition.ServerId, Permission.Read, ct).ConfigureAwait(false)
            ? Ok(definition)
            : Forbid();
    }

    [HttpPost("tasks")]
    public async Task<ActionResult<AiTaskDefinitionDto>> CreateDefinition(
        [FromBody] CreateAiTaskDefinitionRequest request, CancellationToken ct)
    {
        if (!await HasOwnerPermissionAsync(
                request.ProjectId, request.ServerId, Permission.Write, ct).ConfigureAwait(false))
            return Forbid();
        var definition = await service.CreateDefinitionAsync(request, ct).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetDefinition), new { id = definition.Id }, definition);
    }

    [HttpPut("tasks/{id:int}")]
    public async Task<ActionResult<AiTaskDefinitionDto>> UpdateDefinition(
        int id, [FromBody] UpdateAiTaskDefinitionRequest request, CancellationToken ct)
    {
        var existing = await service.GetDefinitionAsync(id, ct).ConfigureAwait(false);
        if (existing is null) return NotFound();
        if (!await HasOwnerPermissionAsync(
                existing.ProjectId, existing.ServerId, Permission.Write, ct).ConfigureAwait(false)
            || !await HasOwnerPermissionAsync(
                request.ProjectId, request.ServerId, Permission.Write, ct).ConfigureAwait(false))
            return Forbid();
        var definition = await service.UpdateDefinitionAsync(id, request, ct).ConfigureAwait(false);
        return definition is null ? NotFound() : Ok(definition);
    }

    [HttpDelete("tasks/{id:int}")]
    public async Task<IActionResult> DeleteDefinition(int id, CancellationToken ct)
    {
        var existing = await service.GetDefinitionAsync(id, ct).ConfigureAwait(false);
        if (existing is null) return NotFound();
        if (!await HasOwnerPermissionAsync(
                existing.ProjectId, existing.ServerId, Permission.Admin, ct).ConfigureAwait(false))
            return Forbid();
        return await service.DeleteDefinitionAsync(id, ct).ConfigureAwait(false)
            ? NoContent()
            : NotFound();
    }

    [HttpPost("tasks/{id:int}/run")]
    public async Task<ActionResult<AiRunStartDto>> RunNow(int id, CancellationToken ct)
    {
        var definition = await service.GetDefinitionAsync(id, ct).ConfigureAwait(false);
        if (definition is null) return NotFound();
        if (!await HasOwnerPermissionAsync(
                definition.ProjectId, definition.ServerId, Permission.Write, ct).ConfigureAwait(false))
            return Forbid();
        var task = await service.RunNowAsync(id, null, ct).ConfigureAwait(false);
        return Ok(new AiRunStartDto { TaskId = task.Id });
    }

    [HttpGet("results")]
    public async Task<ActionResult<PaginatedResult<AiRunResultDto>>> GetResults(
        [FromQuery] int? definitionId, [FromQuery] int? pipelineRunId,
        [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (definitionId.HasValue)
        {
            var definition = await service.GetDefinitionAsync(definitionId.Value, ct).ConfigureAwait(false);
            if (definition is null) return NotFound();
            if (!await HasOwnerPermissionAsync(
                    definition.ProjectId, definition.ServerId, Permission.Read, ct).ConfigureAwait(false))
                return Forbid();
        }
        else if (pipelineRunId.HasValue)
        {
            var runPipelineId = await service.GetRunPipelineIdAsync(pipelineRunId.Value, ct).ConfigureAwait(false);
            if (runPipelineId is null) return NotFound();
            if (!await authorization.HasPermissionAsync(
                    User, ResourceType.Pipeline, runPipelineId.Value, Permission.Read, ct).ConfigureAwait(false))
                return Forbid();
        }
        else if (!User.IsInRole("Admin"))
        {
            return Forbid();
        }
        return Ok(await service.GetResultsAsync(
            definitionId, pipelineRunId, request, ct).ConfigureAwait(false));
    }

    [HttpGet("consumption")]
    public async Task<ActionResult<AiConsumptionDto>> GetConsumption(
        [FromQuery] int? projectId, CancellationToken ct)
    {
        if (!projectId.HasValue && !User.IsInRole("Admin"))
            return Forbid();
        if (projectId.HasValue
            && !await authorization.HasPermissionAsync(
                User, ResourceType.Project, projectId, Permission.Read, ct).ConfigureAwait(false))
            return Forbid();
        return Ok(await service.GetConsumptionAsync(projectId, ct).ConfigureAwait(false));
    }

    [HttpPost("results")]
    [Authorize(Policy = "AgentToken")]
    public async Task<ActionResult<AiRunResultDto>> PublishResult(
        [FromBody] PublishAiRunResultRequest request, CancellationToken ct)
    {
        var serverId = await taskService.GetTaskServerIdAsync(request.ServerTaskId, ct)
            .ConfigureAwait(false);
        if (serverId is null) return NotFound();
        if (!IsAgentAuthorizedForServer(serverId.Value)) return Forbid();
        return Ok(await service.PublishResultAsync(request, ct).ConfigureAwait(false));
    }

    private Task<bool> HasOwnerPermissionAsync(
        int? projectId, int? serverId, Permission permission, CancellationToken ct) =>
        projectId.HasValue
            ? authorization.HasPermissionAsync(User, ResourceType.Project, projectId, permission, ct)
            : authorization.HasPermissionAsync(User, ResourceType.Server, serverId, permission, ct);

    private bool IsAgentAuthorizedForServer(int serverId)
    {
        var claim = User.FindFirst("ServerId")?.Value;
        return claim is not null
            && int.TryParse(claim, out var agentServerId)
            && agentServerId == serverId;
    }
}
