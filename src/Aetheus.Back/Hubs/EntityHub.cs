// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Hubs;

/// <summary>
/// Generic SignalR hub for broadcasting CRUD-level changes on resources that don't
/// have a dedicated hub (Project, Vault, VariableLibrary, ...). Clients call
/// <see cref="JoinEntityUpdates"/> with the resource type they're listing and
/// receive an <c>EntityChanged(type, id, op)</c> message on every Create/Update/Delete.
/// </summary>
[Authorize]
public class EntityHub(IResourceAuthorizationService authz) : Hub
{
    // Org-scoped types (Project) gain a per-org aggregate group so a non-admin org member receives the
    // EntityChanged(Created) for a resource that did not exist when they joined - the per-resource group
    // is empty at creation time, so otherwise only admins would see it. Mirrors ServerHub's org fan-out.
    private static bool IsOrgScoped(ResourceType type) =>
        type is ResourceType.Project or ResourceType.Server or ResourceType.Pipeline
            or ResourceType.PipelineTemplate;

    public async Task JoinEntityUpdates(ResourceType type)
    {
        var ids = await authz.GetAccessibleResourceIdsAsync(Context.User!, type, Permission.Read).ConfigureAwait(false);
        if (ids is null)
        {
            // Admin / wildcard - global aggregate group covers everything for this type.
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.EntityAll(type)).ConfigureAwait(false);
            return;
        }
        var orgIds = IsOrgScoped(type)
            ? await authz.GetOrganizationIdsWithPermissionAsync(Context.User!, type, Permission.Read).ConfigureAwait(false)
            : [];
        if (ids.Count == 0 && orgIds.Count == 0)
            throw new HubException("Access denied.");
        foreach (var id in ids)
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Entity(type, id)).ConfigureAwait(false);
        foreach (var orgId in orgIds)
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.EntityOrg(type, orgId)).ConfigureAwait(false);
    }

    public async Task LeaveEntityUpdates(ResourceType type)
    {
        var ids = await authz.GetAccessibleResourceIdsAsync(Context.User!, type, Permission.Read).ConfigureAwait(false);
        if (ids is null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.EntityAll(type)).ConfigureAwait(false);
            return;
        }
        var orgIds = IsOrgScoped(type)
            ? await authz.GetOrganizationIdsWithPermissionAsync(Context.User!, type, Permission.Read).ConfigureAwait(false)
            : [];
        foreach (var id in ids)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.Entity(type, id)).ConfigureAwait(false);
        foreach (var orgId in orgIds)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.EntityOrg(type, orgId)).ConfigureAwait(false);
    }
}
