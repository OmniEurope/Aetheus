// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

public class PermissionService
{
    private List<EffectivePermissionDto> _permissions = [];
    private List<EffectivePermissionKeyDto> _keys = [];
    private bool _isAdmin;

    public bool IsLoaded { get; private set; }

    public event Action? OnPermissionsChanged;

    /// <summary>Sets the user's levels and, when given, its named permissions (R-11); a caller that only
    /// knows the levels (<paramref name="keys"/> null) leaves the named permissions as they were.</summary>
    public void SetPermissions(List<EffectivePermissionDto> permissions, bool isAdmin, List<EffectivePermissionKeyDto>? keys = null)
    {
        _permissions = permissions;
        _isAdmin = isAdmin;
        if (keys is not null) _keys = keys;
        IsLoaded = true;
        OnPermissionsChanged?.Invoke();
    }

    public void Clear()
    {
        _permissions = [];
        _keys = [];
        _isAdmin = false;
        IsLoaded = false;
        OnPermissionsChanged?.Invoke();
    }

    /// <summary>
    /// R-11: true when the user holds the named permission <paramref name="key"/> on every resource of its
    /// type, or on <paramref name="resourceId"/>. A level key is the level itself (the same answer as
    /// <see cref="HasPermission"/>); a site Admin holds every key. Like the level checks, this drives what
    /// the UI offers; the backend decides.
    /// </summary>
    public bool Can(string key, int? resourceId = null)
    {
        if (_isAdmin) return true;
        var definition = PermissionCatalog.Find(key);
        if (definition is null) return false;
        if (definition is { Level: { } level, ResourceType: { } type })
            return HasPermission(type, resourceId, level);
        return _keys.Any(held => string.Equals(held.Key, key, StringComparison.Ordinal)
            && (held.ResourceId is null || held.ResourceId == resourceId));
    }

    /// <summary>The named permissions (R-11) the user holds, as last loaded.</summary>
    public IReadOnlyList<EffectivePermissionKeyDto> GetPermissionKeys() => _keys;

    public bool CanRead(ResourceType resourceType, int? resourceId = null)
        => HasPermission(resourceType, resourceId, Permission.Read);

    public bool CanWrite(ResourceType resourceType, int? resourceId = null)
        => HasPermission(resourceType, resourceId, Permission.Write);

    public bool CanAdmin(ResourceType resourceType, int? resourceId = null)
        => HasPermission(resourceType, resourceId, Permission.Admin);

    public bool CanReadAny(ResourceType resourceType)
        => _isAdmin || _permissions.Any(p =>
            p.ResourceType == resourceType
            && p.Permission >= Permission.Read);

    public bool HasPermission(ResourceType resourceType, int? resourceId, Permission required)
    {
        if (_isAdmin) return true;

        foreach (var perm in _permissions)
        {
            if (perm.ResourceType != resourceType) continue;
            if ((int)perm.Permission < (int)required) continue;

            // Wildcard (null ResourceId) covers all resources
            if (perm.ResourceId is null) return true;

            // Specific resource match
            if (resourceId.HasValue && perm.ResourceId == resourceId.Value) return true;
        }

        return false;
    }

    public List<EffectivePermissionDto> GetPermissions() => _permissions;
}
