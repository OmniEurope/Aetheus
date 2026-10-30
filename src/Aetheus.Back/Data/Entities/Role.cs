// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

public class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>When the role was created; roles that existed before this column took the migration's run time.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last change of the role itself: its name, its description or its permissions.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>The organization a role created by its administrators belongs to: it is held for that
    /// organization only and administered by it. Null for the built-in roles and the roles a site Admin
    /// creates for every organization.</summary>
    public int? OrganizationId { get; set; }

    // Navigation
    public Organization? Organization { get; set; }
    public List<UserRole> UserRoles { get; set; } = [];
    public List<UserOrganizationRole> OrganizationAssignments { get; set; } = [];
    public List<ResourcePermission> ResourcePermissions { get; set; } = [];
    public List<RolePermissionGrant> PermissionGrants { get; set; } = [];
}
