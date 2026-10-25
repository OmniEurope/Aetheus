// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services;

/// <summary>
/// Operation kind for <see cref="IEntityChangeNotifier"/>. Strings are used as the
/// payload contract for clients (case-sensitive).
/// </summary>
public static class EntityChangeOps
{
    public const string Created = "Created";
    public const string Updated = "Updated";
    public const string Deleted = "Deleted";
}

public interface IEntityChangeNotifier
{
    /// <param name="organizationId">
    /// Owning organization id for org-scoped types (Project). When supplied, the event also targets the
    /// per-org aggregate group so a non-admin org member receives a Created event for a resource that did
    /// not exist when they joined (its per-resource group is empty at creation time). Omit for
    /// non-org-scoped types or when the org is unknown.
    /// </param>
    Task BroadcastAsync(ResourceType type, int id, string op, CancellationToken ct = default, int? organizationId = null);
    /// <summary>Broadcasts a domain-specific event without masquerading as an entity CRUD change.</summary>
    Task BroadcastOperationalAsync(
        ResourceType type,
        int id,
        string eventName,
        CancellationToken ct = default,
        int? organizationId = null);
}
