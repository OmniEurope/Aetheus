// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Hubs;

[Authorize]
public class ServerHub(IResourceAuthorizationService authz) : Hub
{
    public async Task JoinServerGroup(int serverId)
    {
        if (!await authz.HasPermissionAsync(Context.User!, ResourceType.Server, serverId, Permission.Read).ConfigureAwait(false))
            throw new HubException("Access denied.");

        await Groups.AddToGroupAsync(Context.ConnectionId, $"server-{serverId}").ConfigureAwait(false);
    }

    public Task LeaveServerGroup(int serverId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"server-{serverId}");

    public async Task JoinAllServers()
    {
        // F-05: avoid leaking cross-resource updates. Admins (null = all) keep the aggregate group;
        // every other user is fanned out into per-resource groups they can actually read.
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(Context.User!, ResourceType.Server, Permission.Read).ConfigureAwait(false);
        if (accessibleIds is null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.AllServers).ConfigureAwait(false);
            return;
        }
        // Per-org aggregate groups close the CREATE blind spot: a server enrolled AFTER this connection
        // has an empty server-{id} group, so its ServerRegistered would otherwise reach only admins.
        // Only the organizations whose role grants Read on every server get the org group (ADR-059).
        var orgIds = await authz.GetOrganizationIdsWithPermissionAsync(Context.User!, ResourceType.Server, Permission.Read).ConfigureAwait(false);
        if (accessibleIds.Count == 0 && orgIds.Count == 0)
            throw new HubException("Access denied.");
        foreach (var id in accessibleIds)
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Server(id)).ConfigureAwait(false);
        foreach (var orgId in orgIds)
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.ServerOrg(orgId)).ConfigureAwait(false);
    }

    public async Task LeaveAllServers()
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(Context.User!, ResourceType.Server, Permission.Read).ConfigureAwait(false);
        if (accessibleIds is null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.AllServers).ConfigureAwait(false);
            return;
        }
        var orgIds = await authz.GetOrganizationIdsWithPermissionAsync(Context.User!, ResourceType.Server, Permission.Read).ConfigureAwait(false);
        foreach (var id in accessibleIds)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.Server(id)).ConfigureAwait(false);
        foreach (var orgId in orgIds)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.ServerOrg(orgId)).ConfigureAwait(false);
    }
}
