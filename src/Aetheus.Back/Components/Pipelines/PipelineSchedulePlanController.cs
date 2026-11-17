// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Aetheus.Back.Components.Pipelines;

[ApiController]
[Route("api/pipelines")]
[Authorize]
public sealed class PipelineSchedulePlanController(
    IPipelineSchedulePlanService planService,
    IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary>The organization's scheduled pipelines over <c>[from, to)</c>, as the fleet listing
    /// scopes them: a member of the organization (or a site Admin), and only the pipelines it may read.</summary>
    [HttpGet("schedule-plan")]
    public async Task<ActionResult<SchedulePlanDto>> GetSchedulePlan(
        [FromQuery, BindRequired] int organizationId,
        [FromQuery, BindRequired] DateTime from,
        [FromQuery, BindRequired] DateTime to,
        [FromQuery(Name = "sourceBranch")] string[]? sourceBranch,
        CancellationToken ct)
    {
        if (!User.IsInRole(RoleNames.Admin)
            && !(await authz.GetUserOrganizationIdsAsync(User, ct)).Contains(organizationId))
            return Forbid();
        // R-5: the branch the reader chose per pipeline to see its schedule on; it changes what the plan shows only.
        var sourceBranches = PipelineScheduleSourceBranches.Parse(sourceBranch);
        var accessiblePipelineIds = await authz.GetAccessibleResourceIdsAsync(
            User, ResourceType.Pipeline, Permission.Read, ct);
        // R-8: the other scheduled actions follow the rights their own endpoints ask for. A server's scan: read on the
        // server to see it, the Administrator right (the one the schedule write asks for) to move it. An AI task: read
        // on its owner (project or server) to see it, write on that owner to move it. A backup: read on its project to
        // see it, write on the project to move it.
        var serverActions = new ServerActionScope(
            await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Server, Permission.Read, ct),
            await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Server, Permission.Admin, ct))
        {
            ReadableProjectIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Project, Permission.Read, ct),
            WritableProjectIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Project, Permission.Write, ct),
            WritableServerIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Server, Permission.Write, ct),
            // The CVE watch is global data and its sources page is for site administrators: only they see it here, read only.
            SeesCveWatch = User.IsInRole(RoleNames.Admin)
        };
        return Ok(await planService.GetPlanAsync(organizationId, from, to, accessiblePipelineIds, serverActions, sourceBranches, ct));
    }

    /// <summary>What a proposed schedule (<paramref name="frequency"/>, <paramref name="day"/>,
    /// <paramref name="hour"/> and <paramref name="minute"/> in UTC) would give for the pipeline over
    /// <c>[from, to)</c>: its occurrences, its overlaps with the other scheduled pipelines, and whether the
    /// YAML would take the change. Writes nothing. Same read rule as the plan: read on the pipeline, and a
    /// member of its organization (or a site Admin); a pipeline of no organization is for an Admin only.</summary>
    [HttpGet("{id:int}/schedule-preview")]
    public async Task<ActionResult<SchedulePreviewDto>> GetSchedulePreview(
        int id,
        [FromQuery, BindRequired] ScheduleFrequency frequency,
        [FromQuery] int? day,
        [FromQuery, BindRequired] int hour,
        [FromQuery, BindRequired] int minute,
        [FromQuery, BindRequired] DateTime from,
        [FromQuery, BindRequired] DateTime to,
        CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, id, Permission.Read, ct))
            return Forbid();

        var target = await planService.FindTargetAsync(id, ct);
        if (target is null) return NotFound();
        if (!User.IsInRole(RoleNames.Admin)
            && !(target.OrganizationId is { } organizationId
                && (await authz.GetUserOrganizationIdsAsync(User, ct)).Contains(organizationId)))
            return Forbid();

        var accessiblePipelineIds = await authz.GetAccessibleResourceIdsAsync(
            User, ResourceType.Pipeline, Permission.Read, ct);
        return Ok(await planService.PreviewAsync(
            target, new ScheduleFrequencySpec(frequency, day, hour, minute), from, to, accessiblePipelineIds, ct));
    }
}
