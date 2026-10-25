// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Aetheus.Back.Hubs;

/// <summary>
/// Pushes <c>EntityChanged(type, id, op)</c> messages over <see cref="EntityHub"/>.
/// Targets the aggregate group (admins / wildcards), the per-resource group (users with explicit
/// access) and, for org-scoped types, the per-org group (org members) so subscribers receive only
/// events they're allowed to see.
/// </summary>
public sealed class EntityChangeNotifier(IHubContext<EntityHub> hub, ILogger<EntityChangeNotifier> logger) : IEntityChangeNotifier
{
    // Types whose change events must also fan out to the per-org aggregate group. Keep in lockstep with
    // the HubGroups.EntityOrg routing: a caller that omits organizationId for one of these silently
    // strands org members who have not joined the per-resource group yet (the F-05 regression class).
    private static readonly HashSet<ResourceType> OrgScopedTypes =
        [ResourceType.Project, ResourceType.Pipeline, ResourceType.PipelineTemplate];

    public Task BroadcastAsync(ResourceType type, int id, string op, CancellationToken ct = default, int? organizationId = null)
    {
        if (organizationId is null && OrgScopedTypes.Contains(type))
        {
            logger.LogWarning(
                "EntityChanged broadcast for org-scoped {Type} {Id} omitted organizationId; org members may miss this {Op} event",
                type, id, op);
        }

        var groups = organizationId is { } orgId
            ? new[] { HubGroups.EntityAll(type), HubGroups.Entity(type, id), HubGroups.EntityOrg(type, orgId) }
            : [HubGroups.EntityAll(type), HubGroups.Entity(type, id)];
        return hub.Clients.Groups(groups).SendAsync("EntityChanged", type, id, op, ct);
    }

    public Task BroadcastOperationalAsync(
        ResourceType type,
        int id,
        string eventName,
        CancellationToken ct = default,
        int? organizationId = null)
    {
        if (organizationId is null && OrgScopedTypes.Contains(type))
        {
            logger.LogWarning(
                "Operational broadcast {EventName} for org-scoped {Type} {Id} omitted organizationId; org members may miss it",
                eventName, type, id);
        }

        var groups = organizationId is { } orgId
            ? new[] { HubGroups.EntityAll(type), HubGroups.Entity(type, id), HubGroups.EntityOrg(type, orgId) }
            : [HubGroups.EntityAll(type), HubGroups.Entity(type, id)];
        return hub.Clients.Groups(groups).SendAsync(eventName, id, ct);
    }
}
