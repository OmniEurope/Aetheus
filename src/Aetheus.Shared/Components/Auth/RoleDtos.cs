// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Auth;

public sealed record RoleDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int PermissionCount { get; init; }
    public int UserCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public List<ResourcePermissionDto> Permissions { get; init; } = [];

    /// <summary>The organization an organization's own role belongs to (held and administered there only);
    /// null for a built-in role or a role a site Admin made for every organization.</summary>
    public int? OrganizationId { get; init; }
    public string? OrganizationName { get; init; }

    /// <summary>The named permissions (R-11) the role holds on its whole organization: the level keys of
    /// its organization-wide levels, and its action keys.</summary>
    public List<string> PermissionKeys { get; init; } = [];
}

public sealed record CreateRoleRequest
{
    [Required]
    [StringLength(50)]
    public string Name { get; init; } = string.Empty;

    [StringLength(200)]
    public string Description { get; init; } = string.Empty;

    /// <summary>The named permissions (R-11) the new role starts with, on its whole organization.</summary>
    [MaxLength(200)]
    [MaxItemStringLength(80)]
    public List<string>? PermissionKeys { get; init; }

    /// <summary>The organization the role belongs to: required from an organization's administrator, whose
    /// roles are always its organization's; a site Admin leaves it empty for a role of every organization.</summary>
    [Range(1, int.MaxValue)]
    public int? OrganizationId { get; init; }
}

/// <summary>Replaces the organization-wide named permissions (R-11) of a role: the level keys set its
/// organization-wide levels, the action keys its action grants. Grants naming one resource are kept.</summary>
public sealed record SetRolePermissionKeysRequest
{
    [Required]
    [MaxLength(200)]
    [MaxItemStringLength(80)]
    public List<string> Keys { get; init; } = [];
}

public sealed record UpdateRoleRequest
{
    [Required]
    [StringLength(50)]
    public string Name { get; init; } = string.Empty;

    [StringLength(200)]
    public string Description { get; init; } = string.Empty;
}

/// <summary>A user that holds a given role, for the role detail "Users" tab.</summary>
/// <remarks>One row per assignment: a user holding the role for two organizations appears twice;
/// <paramref name="OrganizationId"/> is null for a site-wide (Admin) assignment.</remarks>
public sealed record RoleUserDto(
    int UserId, string Username, string? Email, bool IsActive,
    int? OrganizationId = null, string? OrganizationName = null);

/// <summary>Request to add an existing user to a role, for one organization (ADR-059). Only the
/// Admin role is held site-wide, with no organization.</summary>
public sealed record AddUserToRoleRequest
{
    [Range(1, int.MaxValue)]
    public int UserId { get; init; }

    [Range(1, int.MaxValue)]
    public int? OrganizationId { get; init; }
}

public sealed record ResourcePermissionDto
{
    public int Id { get; init; }
    public ResourceType ResourceType { get; init; }
    public int? ResourceId { get; init; }
    public string? ResourceName { get; init; }
    public Permission Permission { get; init; }
}

public sealed record SetResourcePermissionsRequest
{
    [Required]
    [MaxLength(200)]
    public List<ResourcePermissionEntry> Permissions { get; init; } = [];
}

public sealed record ResourcePermissionEntry
{
    public ResourceType ResourceType { get; init; }
    public int? ResourceId { get; init; }
    public Permission Permission { get; init; }
}

public sealed record UserPermissionSummaryDto
{
    public int UserId { get; init; }
    public string Username { get; init; } = string.Empty;
    public List<string> Roles { get; init; } = [];
    public List<EffectivePermissionDto> EffectivePermissions { get; init; } = [];

    /// <summary>The named permissions (R-11) the user holds, level keys included.</summary>
    public List<EffectivePermissionKeyDto> PermissionKeys { get; init; } = [];
}

/// <summary>One named permission (R-11) a user holds: on every resource of its organization when
/// <paramref name="ResourceId"/> is null, else on that resource only.</summary>
/// <param name="OrganizationId">The organization the granting role is held for; null for a site-wide (Admin) grant.</param>
public sealed record EffectivePermissionKeyDto(string Key, int? ResourceId, int? OrganizationId);

public sealed record EffectivePermissionDto
{
    public ResourceType ResourceType { get; init; }
    public int? ResourceId { get; init; }
    public string? ResourceName { get; init; }
    public Permission Permission { get; init; }
    public string GrantedByRole { get; init; } = string.Empty;

    /// <summary>The organization the granting role is held for; null for a site-wide (Admin) grant.</summary>
    public int? OrganizationId { get; init; }
    public string? OrganizationName { get; init; }
}
