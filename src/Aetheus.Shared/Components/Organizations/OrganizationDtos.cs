// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Organizations;

public record OrganizationDto(
    int Id,
    string Name,
    string Slug,
    string Description,
    int MemberCount,
    int ProjectCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record OrganizationDetailDto(
    int Id,
    string Name,
    string Slug,
    string Description,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<OrganizationMemberDto> Members,
    List<OrganizationProjectDto> Projects,
    bool AutoCreateDevelopBranch = false);

public record OrganizationMemberDto(
    int Id,
    int UserId,
    string Username,
    string? Email,
    OrganizationRole Role,
    DateTime CreatedAt);

public record OrganizationProjectDto(
    int Id,
    string Name,
    ProjectStatus Status);

/// <summary>
/// Lightweight projection of an organization the current user is a member of, used by the
/// header organization picker. Includes the user's role within that organization so the UI
/// can render a badge or restrict actions.
/// </summary>
/// <summary>An organization the caller administers (every organization for a site Admin): what the
/// organization switcher of the Administration section offers. <see cref="Keys"/> are the organization-administration
/// keys the caller holds in it (all of them for a site Admin): a page lists the organizations holding its own key.</summary>
public sealed record AdministeredOrganizationDto(int Id, string Name, IReadOnlyList<string> Keys)
{
    /// <summary>True when the caller holds <paramref name="organizationKey"/> in this organization.</summary>
    public bool Holds(string organizationKey) => Keys.Contains(organizationKey, StringComparer.Ordinal);
}

public record MyOrganizationDto(
    int Id,
    string Name,
    string Slug,
    OrganizationRole Role);

/// <summary>
/// An organization a specific user (by id) is a member of, for the user detail "Organizations"
/// tab. Carries <see cref="MemberId"/> so the page can call the existing member endpoints
/// (<c>PUT/DELETE api/organizations/{orgId}/members/{memberId}</c>) to change the role or remove.
/// </summary>
public record UserOrganizationDto(
    int OrganizationId,
    int MemberId,
    string Name,
    string Slug,
    OrganizationRole Role);

public abstract record OrganizationMutationRequest
{
    [Required, StringLength(100, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional: an empty slug is generated from the name by the back end (R-22).</summary>
    [StringLength(100)]
    [RegularExpression("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$",
        ErrorMessage = "Slug must be lowercase alphanumeric with optional hyphens.")]
    public string Slug { get; init; } = string.Empty;

    [StringLength(500)]
    public string Description { get; init; } = string.Empty;

    /// <summary>Whether a project of this organization gets a <c>develop</c> branch (cut from the default branch) when its
    /// internal repository receives its first commit. Off by default (R-76).</summary>
    public bool AutoCreateDevelopBranch { get; init; }
}

public sealed record CreateOrganizationRequest : OrganizationMutationRequest;

public sealed record UpdateOrganizationRequest : OrganizationMutationRequest;

public sealed record AddOrganizationMemberRequest
{
    /// <summary>The user to add, by id: a site Admin only (ADR-059). An Organization Admin names the
    /// user by <see cref="UserLogin"/> instead, so it cannot walk the global user ids.</summary>
    public int? UserId { get; init; }

    /// <summary>The exact username or e-mail address of the user to add.</summary>
    [StringLength(200)]
    public string? UserLogin { get; init; }

    [Required]
    [EnumDataType(typeof(OrganizationRole))]
    public OrganizationRole Role { get; init; } = OrganizationRole.Member;
}

public sealed record UpdateOrganizationMemberRequest
{
    [Required]
    [EnumDataType(typeof(OrganizationRole))]
    public OrganizationRole Role { get; init; }
}

public sealed record AssignProjectsRequest
{
    [Required]
    [MaxLength(1000)]
    public List<int> ProjectIds { get; init; } = [];
}
