// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

[ApiController]
[Route("api/pipelines")]
[Authorize]
public sealed class PipelineScheduleWriteController(
    IPipelineService pipelineService,
    IPipelineScheduleWriter writer,
    IPipelineOwnerAuthorization ownerAuthz,
    IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary>Moves, enables or disables the pipeline's schedule, in its YAML. The same rights as
    /// <c>PUT api/pipelines/{id}</c>, which writes that same YAML: write on the pipeline, write on pipelines
    /// in the organization of its project, the rights over its owner, and the run right when the schedule is turned on. The caller's rights are checked
    /// here; the owner of the pipeline stays (<see cref="PipelineScheduleOnlyEdit"/>).</summary>
    [HttpPut("{id:int}/schedule")]
    public async Task<ActionResult<PipelineDto>> UpdateSchedule(
        int id, [FromBody] UpdatePipelineScheduleRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, id, Permission.Write, ct))
            return Forbid();

        var existing = await pipelineService.GetPipelineAsync(id, ct);
        if (existing is null) return NotFound();
        if (!await authz.CanCreateInProjectAsync(User, ResourceType.Pipeline, existing.ProjectId, ct))
            return Forbid();
        if (!await ownerAuthz.CanOwnAsync(User, existing.ProjectId, existing.EnvironmentId, existing.ProjectServerId, ct))
            return Forbid();

        // Turning a schedule on starts runs on its own: it takes the right to run the pipeline. Same rule as
        // PUT api/pipelines/{id}. Moving an enabled schedule or disabling it takes write only.
        var change = PipelineScheduleWriter.Prepare(existing, request);
        if (PipelineScheduleOnlyEdit.TurnsScheduleOn(existing.YamlDefinition, change.Yaml)
            && !await authz.HasPermissionAsync(User, PermissionKeys.PipelineRun, id, ct))
            return Forbid();

        return Ok(await writer.ApplyAsync(existing, change, request.Enabled, ct));
    }

    /// <summary>Moves the branch the pipeline's definition is read on (recette R-5, user decision 2026-11-09), the planner's
    /// selector. The rights of <see cref="UpdateSchedule"/>, plus the run right: the definition on the other branch is adopted
    /// and may carry a schedule that starts runs on its own, which the file read here does not tell beforehand.</summary>
    [HttpPut("{id:int}/source-branch")]
    public async Task<ActionResult<PipelineDto>> UpdateSourceBranch(
        int id, [FromBody] UpdatePipelineSourceBranchRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, id, Permission.Write, ct))
            return Forbid();

        var existing = await pipelineService.GetPipelineAsync(id, ct);
        if (existing is null) return NotFound();
        if (!await authz.CanCreateInProjectAsync(User, ResourceType.Pipeline, existing.ProjectId, ct))
            return Forbid();
        if (!await ownerAuthz.CanOwnAsync(User, existing.ProjectId, existing.EnvironmentId, existing.ProjectServerId, ct))
            return Forbid();
        if (!await authz.HasPermissionAsync(User, PermissionKeys.PipelineRun, id, ct))
            return Forbid();

        return Ok(await writer.ChangeSourceBranchAsync(existing, request.Branch, request.ExpectedBranch, ct));
    }
}
