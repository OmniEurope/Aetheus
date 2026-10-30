// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>
/// A role held by a user for one organization (ADR-059): the role's permissions apply only to the
/// resources that organization owns. Site-wide assignments stay in <see cref="UserRole"/> and are honoured
/// for the Admin role only.
/// </summary>
public class UserOrganizationRole
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int OrganizationId { get; set; }
    public int RoleId { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
    public Organization Organization { get; set; } = null!;
    public Role Role { get; set; } = null!;
}
