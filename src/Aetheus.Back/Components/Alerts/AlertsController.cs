// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Alerts;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class AlertsController(
    IAlertService alertService,
    IAlertOccurrenceService occurrences,
    IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary>One alert that fired, the address the notification mail's button opens (recette R-52).
    /// An alert about a server is shown only to who may read that server.</summary>
    [HttpGet("occurrences/{id:long}")]
    public async Task<ActionResult<AlertOccurrenceDto>> GetOccurrence(long id, CancellationToken ct)
    {
        var occurrence = await occurrences.GetAsync(id, ct);
        if (occurrence is null) return NotFound();
        // An alert about no server (a platform alert) has none to read: the controller's Admin gate is its only gate.
        if (occurrence.ServerId is not { } serverId) return Ok(occurrence);
        return await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct)
            ? Ok(occurrence)
            : Forbid();
    }

    [HttpGet]
    public async Task<ActionResult<List<AlertRuleDto>>> GetAlertRules(CancellationToken ct)
    {
        return Ok(await alertService.GetAlertRulesAsync(ct));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<AlertRuleDto>> GetAlertRule(int id, CancellationToken ct)
    {
        var rule = await alertService.GetAlertRuleAsync(id, ct);
        if (rule is null) return NotFound();
        return Ok(rule);
    }

    [HttpPost]
    public async Task<ActionResult<AlertRuleDto>> CreateAlertRule(
        [FromBody] CreateAlertRuleRequest request, CancellationToken ct)
    {
        var rule = await alertService.CreateAlertRuleAsync(request, ct);
        return CreatedAtAction(nameof(GetAlertRule), new { id = rule.Id }, rule);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<AlertRuleDto>> UpdateAlertRule(
        int id, [FromBody] UpdateAlertRuleRequest request, CancellationToken ct)
    {
        var rule = await alertService.UpdateAlertRuleAsync(id, request, ct);
        if (rule is null) return NotFound();
        return Ok(rule);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteAlertRule(int id, CancellationToken ct)
    {
        var deleted = await alertService.DeleteAlertRuleAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }
}
