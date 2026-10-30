// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Organizations;
namespace Aetheus.Back.Components.Organizations;

public interface IOrganizationService
{
    Task<PaginatedResult<OrganizationDto>> GetOrganizationsAsync(string? search, PaginationRequest request, CancellationToken ct);

    /// <summary>The list limited to <paramref name="organizationIds"/> (the organizations an organization's
    /// administrator administers); null lists every organization.</summary>
    Task<PaginatedResult<OrganizationDto>> GetOrganizationsAsync(
        string? search, PaginationRequest request, IReadOnlyList<int>? organizationIds, CancellationToken ct);

    /// <summary>The organizations named (every organization when null), for the organization switcher.</summary>
    Task<List<AdministeredOrganizationDto>> GetOrganizationNamesAsync(IReadOnlyList<int>? organizationIds, CancellationToken ct);
    Task<OrganizationDetailDto?> GetOrganizationAsync(int id, CancellationToken ct);
    Task<OrganizationDto> CreateOrganizationAsync(CreateOrganizationRequest request, CancellationToken ct);
    Task<OrganizationDto?> UpdateOrganizationAsync(int id, UpdateOrganizationRequest request, CancellationToken ct);
    Task<bool> DeleteOrganizationAsync(int id, CancellationToken ct);

    Task<OrganizationMemberDto> AddMemberAsync(int organizationId, AddOrganizationMemberRequest request, CancellationToken ct);
    Task<OrganizationMemberDto?> UpdateMemberAsync(int organizationId, int memberId, UpdateOrganizationMemberRequest request, CancellationToken ct);
    Task<bool> RemoveMemberAsync(int organizationId, int memberId, CancellationToken ct);

    Task AssignProjectsAsync(int organizationId, List<int> projectIds, CancellationToken ct);

    /// <summary>
    /// Returns the organizations the given user is a member of (id, name, slug, role).
    /// Drives the header organization picker.
    /// </summary>
    Task<List<MyOrganizationDto>> GetMyOrganizationsAsync(string username, CancellationToken ct);

    /// <summary>
    /// Returns the organizations a specific user (by id) is a member of, with member row ids.
    /// Drives the user detail "Organizations" tab.
    /// </summary>
    Task<List<UserOrganizationDto>> GetOrganizationsForUserAsync(int userId, CancellationToken ct);

    /// <summary>
    /// Returns the id of the default (first) organization, or null if none exist.
    /// Used by cross-module callers that need to assign an org-scoped entity.
    /// </summary>
    Task<int?> GetDefaultOrganizationIdAsync(CancellationToken ct);
}
