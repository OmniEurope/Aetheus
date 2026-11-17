// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Back.Components.SystemLogs;

/// <summary>
/// An organization's exclusion rules of the system logs (R-28). Reading them takes org.logrules.view on the
/// organization (a Reader's), creating, switching and deleting one org.logrules.edit (a Contributor's); the
/// site Admin holds every key. A rule of another organization is answered 404, never shown.
/// </summary>
[ApiController]
[Route("api/system-log-rules")]
[Authorize]
public sealed class SystemLogExclusionRulesController(
    ISystemLogExclusionService service, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<SystemLogExclusionRuleDto>>> List(
        [FromQuery, Range(1, int.MaxValue)] int organizationId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.OrganizationLogRulesView, organizationId, ct))
            return Forbid();
        return Ok(await service.ListAsync(organizationId, ct));
    }

    [HttpPost]
    public async Task<ActionResult<SystemLogExclusionRuleDto>> Create(
        [FromQuery, Range(1, int.MaxValue)] int organizationId, [FromBody] SaveSystemLogExclusionRuleRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.OrganizationLogRulesEdit, organizationId, ct))
            return Forbid();
        return Ok(await service.CreateAsync(organizationId, request, User.Identity?.Name, ct));
    }

    [HttpPut("{ruleId:int}/enabled")]
    public async Task<ActionResult<SystemLogExclusionRuleDto>> SetEnabled(
        int ruleId, [FromQuery, Range(1, int.MaxValue)] int organizationId, [FromBody] SetSystemLogExclusionRuleEnabledRequest request,
        CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.OrganizationLogRulesEdit, organizationId, ct))
            return Forbid();
        return Ok(await service.SetEnabledAsync(organizationId, ruleId, request.Enabled, ct));
    }

    [HttpDelete("{ruleId:int}")]
    public async Task<IActionResult> Delete(
        int ruleId, [FromQuery, Range(1, int.MaxValue)] int organizationId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.OrganizationLogRulesEdit, organizationId, ct))
            return Forbid();
        await service.DeleteAsync(organizationId, ruleId, ct);
        return NoContent();
    }
}
