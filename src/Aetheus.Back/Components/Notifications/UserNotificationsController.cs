// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;

namespace Aetheus.Back.Components.Notifications;

/// <summary>Scope inbox operations to the signed-in user and return 404 for another user delivery.
/// Authenticated identities without a user row read an empty inbox and receive 403 for preferences
/// or subscriptions they cannot hold.</summary>
[ApiController]
[Route("api/notifications/me")]
[Authorize]
public sealed class UserNotificationsController(
    IUserNotificationService service,
    IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<NotificationDeliveryDto>>> GetMine(
        [FromQuery] UserNotificationPageRequest request, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId)
            return Ok(new PaginatedResult<NotificationDeliveryDto> { Page = request.Page, PageSize = request.PageSize });
        return Ok(await service.GetDeliveriesAsync(userId, request, ct));
    }

    /// <summary>The event types the caller's /notifications Event filter offers.</summary>
    [HttpGet("filter-values")]
    public async Task<ActionResult<UserNotificationFilterValuesDto>> GetFilterValues(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Ok(new UserNotificationFilterValuesDto());
        return Ok(await service.GetFilterValuesAsync(userId, ct));
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<int>> GetUnreadCount(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Ok(0);
        return Ok(await service.GetUnreadCountAsync(userId, ct));
    }

    [HttpPost("{id:int}/read")]
    [NotResourceScoped("A delivery belongs to the calling user: the service only matches a delivery of their own user id, and answers 404 for anyone else's.")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return NotFound();
        return await service.MarkReadAsync(userId, id, ct) ? NoContent() : NotFound();
    }

    [HttpPost("read-all")]
    public async Task<ActionResult<int>> MarkAllRead(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Ok(0);
        return Ok(await service.MarkAllReadAsync(userId, ct));
    }

    [HttpGet("preferences")]
    public async Task<ActionResult<List<NotificationPreferenceDto>>> GetPreferences(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Ok(new List<NotificationPreferenceDto>());
        return Ok(await service.GetPreferencesAsync(userId, ct));
    }

    [HttpPut("preferences")]
    public async Task<ActionResult<List<NotificationPreferenceDto>>> UpdatePreferences(
        [FromBody] UpdateNotificationPreferencesRequest request, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Forbid();
        return Ok(await service.UpdatePreferencesAsync(userId, request, ct));
    }

    [HttpGet("subscriptions")]
    public async Task<ActionResult<List<ProjectSubscriptionDto>>> GetSubscriptions(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Ok(new List<ProjectSubscriptionDto>());
        return Ok(await service.GetSubscriptionsAsync(userId, ct));
    }

    [HttpPut("subscriptions/{projectId:int}")]
    public async Task<ActionResult<ProjectSubscriptionDto>> Subscribe(int projectId, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Forbid();
        // Same check as reading the project on ProjectsController: following a project is reading it.
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();

        var subscription = await service.SubscribeAsync(userId, projectId, ct);
        return subscription is null ? NotFound() : Ok(subscription);
    }

    /// <summary>Follow every project the caller can read through Project/Read permission and return
    /// all caller subscriptions.</summary>
    [HttpPost("subscriptions/all")]
    public async Task<ActionResult<List<ProjectSubscriptionDto>>> FollowAll(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Forbid();
        var readable = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Project, Permission.Read, ct);
        return Ok(await service.FollowProjectsAsync(userId, readable, ct));
    }

    [NotResourceScoped("Removes only the calling user's own subscription row; a user who lost access to the project must still be able to stop following it.")]
    [HttpDelete("subscriptions/{projectId:int}")]
    public async Task<IActionResult> Unsubscribe(int projectId, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return NotFound();
        return await service.UnsubscribeAsync(userId, projectId, ct) ? NoContent() : NotFound();
    }

    private int? CurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}
