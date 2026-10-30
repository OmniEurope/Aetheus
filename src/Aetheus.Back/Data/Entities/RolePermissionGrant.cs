// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>
/// One action permission (R-11) a role holds: a key of <see cref="PermissionCatalog"/> that is not a level
/// key, on every resource of the organization the role is held for when <see cref="ResourceId"/> is null,
/// else on that resource only. Level keys are not stored here: they are the role's
/// <see cref="ResourcePermission"/> rows, which every level check already reads.
/// </summary>
public class RolePermissionGrant
{
    public int Id { get; set; }
    public int RoleId { get; set; }

    /// <summary>The catalogue key, e.g. <c>server.firewall.manage</c>.</summary>
    public string PermissionKey { get; set; } = string.Empty;

    /// <summary>The resource of the key's resource type the grant is limited to; null for all of them.</summary>
    public int? ResourceId { get; set; }

    // Navigation
    public Role Role { get; set; } = null!;
}
