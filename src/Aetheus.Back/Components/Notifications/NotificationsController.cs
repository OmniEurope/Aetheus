// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Notifications;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class NotificationsController(INotificationService service) : ControllerBase
{
    [HttpGet("channels")]
    public async Task<ActionResult<PaginatedResult<NotificationChannelDto>>> GetChannels(
        [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        return Ok(await service.GetChannelsAsync(request, ct));
    }

    [HttpGet("channels/{id:int}")]
    public async Task<ActionResult<NotificationChannelDto>> GetChannel(int id, CancellationToken ct)
    {
        var channel = await service.GetChannelAsync(id, ct);
        return channel is null ? NotFound() : Ok(channel);
    }

    [HttpPost("channels")]
    public async Task<ActionResult<NotificationChannelDto>> CreateChannel(
        [FromBody] CreateNotificationChannelRequest request, CancellationToken ct)
    {
        var channel = await service.CreateChannelAsync(request, ct);
        return CreatedAtAction(nameof(GetChannel), new { id = channel.Id }, channel);
    }

    [HttpPut("channels/{id:int}")]
    public async Task<ActionResult<NotificationChannelDto>> UpdateChannel(
        int id, [FromBody] UpdateNotificationChannelRequest request, CancellationToken ct)
    {
        var result = await service.UpdateChannelAsync(id, request, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("channels/{id:int}")]
    public async Task<IActionResult> DeleteChannel(int id, CancellationToken ct)
    {
        return await service.DeleteChannelAsync(id, ct) ? NoContent() : NotFound();
    }

    [HttpPost("channels/{id:int}/test")]
    public async Task<ActionResult<NotificationTestResultDto>> TestChannel(int id, CancellationToken ct)
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var userId = int.TryParse(claim, out var parsed) ? parsed : (int?)null;
        var result = await service.TestChannelAsync(id, userId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("rules")]
    public async Task<ActionResult<PaginatedResult<NotificationRuleDto>>> GetRules(
        [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        return Ok(await service.GetRulesAsync(request, ct));
    }

    /// <summary>Recette R-224: the event types and channels the rules grid's column filters offer.</summary>
    [HttpGet("rules/filter-values")]
    public async Task<ActionResult<NotificationAdminFilterValuesDto>> GetRuleFilterValues(CancellationToken ct) =>
        Ok(await service.GetRuleFilterValuesAsync(ct));

    [HttpPost("rules")]
    public async Task<ActionResult<NotificationRuleDto>> CreateRule(
        [FromBody] CreateNotificationRuleRequest request, CancellationToken ct)
    {
        var rule = await service.CreateRuleAsync(request, ct);
        return Ok(rule);
    }

    [HttpPut("rules/{id:int}")]
    public async Task<ActionResult<NotificationRuleDto>> UpdateRule(
        int id, [FromBody] UpdateNotificationRuleRequest request, CancellationToken ct)
    {
        var result = await service.UpdateRuleAsync(id, request, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("rules/{id:int}")]
    public async Task<IActionResult> DeleteRule(int id, CancellationToken ct)
    {
        return await service.DeleteRuleAsync(id, ct) ? NoContent() : NotFound();
    }
}
