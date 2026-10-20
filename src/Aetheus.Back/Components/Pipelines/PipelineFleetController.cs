// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

[ApiController]
[Route("api/pipelines")]
[Authorize]
public sealed class PipelineFleetController(
    IPipelineFleetService fleetService,
    IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet("fleet")]
    public async Task<ActionResult<PaginatedResult<PipelineFleetItemDto>>> GetFleet(
        [FromQuery] PipelineFleetPaginationRequest request, CancellationToken ct)
    {
        var accessiblePipelineIds = await authz.GetAccessibleResourceIdsAsync(
            User, ResourceType.Pipeline, Permission.Read, ct);
        if (accessiblePipelineIds is { Count: 0 })
            return Ok(new PaginatedResult<PipelineFleetItemDto>());
        var organizationIds = User.IsInRole("Admin")
            ? null
            : await authz.GetUserOrganizationIdsAsync(User, ct);
        return Ok(await fleetService.GetAsync(
            request, organizationIds, accessiblePipelineIds, ct));
    }

    /// <summary>The template names the fleet's Template column filter offers, in the fleet's
    /// scope.</summary>
    [HttpGet("fleet/filter-values")]
    public async Task<ActionResult<PipelineFleetFilterValuesDto>> GetFleetFilterValues(CancellationToken ct)
    {
        var accessiblePipelineIds = await authz.GetAccessibleResourceIdsAsync(
            User, ResourceType.Pipeline, Permission.Read, ct);
        if (accessiblePipelineIds is { Count: 0 })
            return Ok(new PipelineFleetFilterValuesDto());
        var organizationIds = User.IsInRole("Admin")
            ? null
            : await authz.GetUserOrganizationIdsAsync(User, ct);
        return Ok(await fleetService.GetFilterValuesAsync(organizationIds, accessiblePipelineIds, ct));
    }

    [HttpGet("{id:int}/fleet-item")]
    public async Task<ActionResult<PipelineFleetItemDto>> GetFleetItem(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, id, Permission.Read, ct))
            return Forbid();
        var item = await fleetService.GetItemAsync(id, ct);
        if (item.TemplateId is { } templateId
            && !await authz.HasPermissionAsync(
                User, ResourceType.PipelineTemplate, templateId, Permission.Read, ct))
            return Forbid();
        return Ok(item);
    }

    [HttpPost("{id:int}/fleet-update/preview")]
    public async Task<ActionResult<PipelineFleetUpdatePreviewDto>> PreviewFleetUpdate(
        int id, [FromBody] PipelineFleetUpdateRequest request, CancellationToken ct)
    {
        if (!await CanUseTemplateAsync(id, Permission.Read, ct)) return Forbid();
        return Ok(await fleetService.PreviewUpdateAsync(id, request.TargetVersion, ct));
    }

    [HttpPost("{id:int}/fleet-update")]
    public async Task<ActionResult<PipelineDto>> ApplyFleetUpdate(
        int id, [FromBody] PipelineFleetUpdateRequest request, CancellationToken ct)
    {
        if (!await CanUseTemplateAsync(id, Permission.Read, ct)) return Forbid();
        return Ok(await fleetService.UpdateAsync(id, request, ct));
    }

    [HttpPost("{id:int}/extract-template")]
    public async Task<ActionResult<PipelineTemplateDto>> ExtractTemplate(
        int id, [FromBody] ExtractPipelineTemplateRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, id, Permission.Write, ct)
            || !await authz.HasPermissionAsync(
                User, ResourceType.PipelineTemplate, null, Permission.Write, ct))
            return Forbid();
        var item = await fleetService.GetItemAsync(id, ct);
        if (!User.IsInRole("Admin"))
        {
            var organizationIds = await authz.GetUserOrganizationIdsAsync(User, ct);
            if (!organizationIds.Contains(item.OrganizationId)) return Forbid();
        }
        return Ok(await fleetService.ExtractAsync(id, request, ct));
    }

    [HttpPost("{id:int}/promote-template")]
    public async Task<ActionResult<PipelineTemplateDto>> PromoteTemplate(
        int id, [FromBody] PromotePipelineTemplateRequest request, CancellationToken ct)
    {
        if (!await CanUseTemplateAsync(id, Permission.Write, ct)) return Forbid();
        return Ok(await fleetService.PromoteAsync(id, request, ct));
    }

    [HttpGet("{id:int}/promote-template/preview")]
    public async Task<ActionResult<PipelinePromotePreviewDto>> PreviewPromoteTemplate(
        int id, CancellationToken ct)
    {
        if (!await CanUseTemplateAsync(id, Permission.Write, ct)) return Forbid();
        return Ok(await fleetService.PreviewPromotionAsync(id, ct));
    }

    private async Task<bool> CanUseTemplateAsync(
        int pipelineId, Permission templatePermission, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(
                User, ResourceType.Pipeline, pipelineId, Permission.Write, ct))
            return false;
        var item = await fleetService.GetItemAsync(pipelineId, ct);
        return item.TemplateId is { } templateId
            && await authz.HasPermissionAsync(
                User, ResourceType.PipelineTemplate, templateId, templatePermission, ct);
    }
}
