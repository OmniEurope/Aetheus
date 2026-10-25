// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Hubs;

[Authorize]
public class LogHub(ITaskServerReader taskService, IResourceAuthorizationService authz) : Hub
{
    public async Task JoinTaskGroup(int taskId)
    {
        var serverId = await taskService.GetTaskServerIdAsync(taskId).ConfigureAwait(false);
        if (serverId is null)
            throw new HubException("Task not found.");

        if (!await authz.HasPermissionAsync(Context.User!, ResourceType.Server, serverId.Value, Permission.Read).ConfigureAwait(false))
            throw new HubException("Access denied.");

        await Groups.AddToGroupAsync(Context.ConnectionId, $"task-{taskId}").ConfigureAwait(false);
    }

    public Task LeaveTaskGroup(int taskId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"task-{taskId}");
}
