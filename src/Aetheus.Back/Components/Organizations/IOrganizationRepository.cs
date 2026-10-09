// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Organizations;

public interface IOrganizationRepository
{
    Task<(List<Organization> Items, int Total)> GetPagedAsync(
        string? search, int page, int pageSize, string? sortBy, bool sortDescending, CancellationToken ct,
        IReadOnlyList<GridFilter>? filters = null);
    Task<Organization?> GetByIdAsync(int id, CancellationToken ct);
    Task<Organization?> GetWithMembersAndProjectsAsync(int id, CancellationToken ct);
    Task<bool> NameExistsAsync(string name, int? excludeId, CancellationToken ct);
    Task<bool> SlugExistsAsync(string slug, int? excludeId, CancellationToken ct);
    Task AddAsync(Organization org, CancellationToken ct);
    Task<bool> DeleteAsync(int id, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);

    Task<OrganizationMember?> GetMemberAsync(int organizationId, int userId, CancellationToken ct);
    Task<OrganizationMember?> GetMemberByIdAsync(int memberId, CancellationToken ct);
    Task AddMemberAsync(OrganizationMember member, CancellationToken ct);
    Task<bool> RemoveMemberAsync(int organizationId, int memberId, CancellationToken ct);

    Task<User?> GetUserByIdAsync(int userId, CancellationToken ct);
    Task<OrganizationMember?> GetMemberWithUserByIdAsync(int memberId, int organizationId, CancellationToken ct);
    Task AssignProjectsAsync(int organizationId, List<int> projectIds, CancellationToken ct);
    Task<int> CountExistingProjectsAsync(List<int> projectIds, CancellationToken ct);
    /// <summary>The distinct organizations that currently own the given projects.</summary>
    Task<List<int>> GetProjectOrganizationIdsAsync(List<int> projectIds, CancellationToken ct);
    Task<List<int>> GetOrganizationIdsForUserAsync(int userId, CancellationToken ct);
    Task<int?> GetDefaultOrganizationIdAsync(CancellationToken ct);

    /// <summary>
    /// Returns every organization the given user is a direct member of, with the membership
    /// role attached. Used by the header organization picker via <c>/api/organizations/me</c>.
    /// </summary>
    Task<List<(Organization Organization, Aetheus.Shared.Components.Shared.OrganizationRole Role)>> GetOrganizationsForUsernameAsync(string username, CancellationToken ct);

    /// <summary>
    /// Returns the organizations a user (by id) is a member of, with the membership role and the
    /// member row id. Drives the user detail "Organizations" tab (admin view of another user).
    /// </summary>
    Task<List<(Organization Organization, Aetheus.Shared.Components.Shared.OrganizationRole Role, int MemberId)>> GetOrganizationsForUserIdAsync(int userId, CancellationToken ct);

    /// <summary>
    /// Returns (user id, username) for every member of an organization. Used to invalidate the
    /// authz cache and push a realtime refresh to all members on org-wide changes (project
    /// reassignment, org deletion).
    /// </summary>
    Task<List<(int UserId, string Username)>> GetMembersForNotificationAsync(int organizationId, CancellationToken ct);
}
