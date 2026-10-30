// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using Aetheus.Shared.Components.Organizations;
namespace Aetheus.Back.Components.Organizations;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
public class OrganizationsController(IOrganizationService service, IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary>Every organization for a site Admin; for an organization's administrator, the organizations
    /// where it holds org.members.manage (Administration section, owner decision 2026-10-28).
    /// <paramref name="organizationId"/> is the organization switcher's choice, refused (403) when not the caller's.</summary>
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<OrganizationDto>>> GetOrganizations(
        [FromQuery] string? search,
        [FromQuery] PaginationRequest request,
        [FromQuery, Range(1, int.MaxValue)] int? organizationId,
        CancellationToken ct)
    {
        var scope = await AdministrationScope.ResolveAsync(authz, User, PermissionKeys.OrganizationMembersManage, organizationId, ct);
        if (scope is null) return Forbid();
        return Ok(await service.GetOrganizationsAsync(search, request, scope.OrganizationIds, ct));
    }

    /// <summary>
    /// The organizations the caller administers, for the organization switcher of the Administration section:
    /// every organization for a site Admin, else those where it holds one of the organization keys. Open to
    /// any authenticated user; empty for one administering none.
    /// </summary>
    [HttpGet("administered")]
    public async Task<ActionResult<List<AdministeredOrganizationDto>>> GetAdministeredOrganizations(CancellationToken ct)
    {
        var keysByOrganization = new Dictionary<int, List<string>>();
        foreach (var key in PermissionCatalog.OrganizationAdministrationKeys)
        {
            var organizations = await authz.GetOrganizationsWithPermissionKeyAsync(User, key, ct);
            if (organizations is null)
            {
                // A site Admin holds every key in every organization.
                var all = await service.GetOrganizationNamesAsync(null, ct);
                return Ok(all.Select(organization => organization with { Keys = PermissionCatalog.OrganizationAdministrationKeys }).ToList());
            }
            foreach (var id in organizations)
            {
                if (!keysByOrganization.TryGetValue(id, out var held)) keysByOrganization[id] = held = [];
                held.Add(key);
            }
        }
        if (keysByOrganization.Count == 0) return Ok(new List<AdministeredOrganizationDto>());
        var named = await service.GetOrganizationNamesAsync([.. keysByOrganization.Keys], ct);
        return Ok(named.Select(organization => organization with { Keys = keysByOrganization[organization.Id] }).ToList());
    }

    /// <summary>
    /// Returns the organizations the current authenticated user is a member of.
    /// Open to any authenticated user (drives the header organization picker) - inherits only the
    /// class-level [Authorize], must NOT carry an Admin role requirement.
    /// </summary>
    [HttpGet("me")]
    public async Task<ActionResult<List<MyOrganizationDto>>> GetMyOrganizations(CancellationToken ct)
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return Unauthorized();
        return Ok(await service.GetMyOrganizationsAsync(username, ct));
    }

    /// <summary>The organization's detail with its members, for a site Admin or a holder of org.members.manage
    /// for it (R-10, R-11: an Organization Admin by default).</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<OrganizationDetailDto>> GetOrganization(int id, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.OrganizationMembersManage, id, ct)) return Forbid();
        var org = await service.GetOrganizationAsync(id, ct);
        if (org is null) return NotFound();
        // RBAC audit, finding 7: as when adding a member (S2), only a site Admin reads members' e-mails;
        // a member's address is not disclosed to the Organization Admins of every organization it joins.
        return Ok(User.IsInRole(RoleNames.Admin)
            ? org
            : org with { Members = org.Members.Select(member => member with { Email = null }).ToList() });
    }

    /// <summary>Organizations a specific user is a member of (drives the user detail Organizations tab).</summary>
    [HttpGet("~/api/users/{userId:int}/organizations")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<UserOrganizationDto>>> GetUserOrganizations(int userId, CancellationToken ct) =>
        Ok(await service.GetOrganizationsForUserAsync(userId, ct));

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<OrganizationDto>> CreateOrganization([FromBody] CreateOrganizationRequest request, CancellationToken ct)
    {
        var org = await service.CreateOrganizationAsync(request, ct);
        return CreatedAtAction(nameof(GetOrganization), new { id = org.Id }, org);
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<OrganizationDto>> UpdateOrganization(int id, [FromBody] UpdateOrganizationRequest request, CancellationToken ct)
    {
        var org = await service.UpdateOrganizationAsync(id, request, ct);
        return org is null ? NotFound() : Ok(org);
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteOrganization(int id, CancellationToken ct)
    {
        var deleted = await service.DeleteOrganizationAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>Adds a member, which gives the role its level maps to (R-45). A site Admin or one of
    /// the holders of org.members.manage for the organization (R-10, R-11).</summary>
    [HttpPost("{id:int}/members")]
    public async Task<ActionResult<OrganizationMemberDto>> AddMember(int id, [FromBody] AddOrganizationMemberRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.OrganizationMembersManage, id, ct)) return Forbid();
        var siteAdmin = User.IsInRole(RoleNames.Admin);
        // S2: an Organization Admin names the user by exact username or e-mail; it cannot walk the
        // global user ids, and never reads back the e-mail of the user it found.
        if (!siteAdmin && request.UserId is not null)
            return BadRequest("An Organization Admin adds a member by exact username or e-mail address.");
        var member = await service.AddMemberAsync(id, request, ct);
        return Ok(siteAdmin ? member : member with { Email = null });
    }

    [HttpPut("{id:int}/members/{memberId:int}")]
    public async Task<ActionResult<OrganizationMemberDto>> UpdateMember(int id, int memberId, [FromBody] UpdateOrganizationMemberRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.OrganizationMembersManage, id, ct)) return Forbid();
        var m = await service.UpdateMemberAsync(id, memberId, request, ct);
        if (m is null) return NotFound();
        return Ok(User.IsInRole(RoleNames.Admin) ? m : m with { Email = null });
    }

    [HttpDelete("{id:int}/members/{memberId:int}")]
    public async Task<IActionResult> RemoveMember(int id, int memberId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.OrganizationMembersManage, id, ct)) return Forbid();
        var removed = await service.RemoveMemberAsync(id, memberId, ct);
        return removed ? NoContent() : NotFound();
    }

    [HttpPut("{id:int}/projects")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> AssignProjects(int id, [FromBody] AssignProjectsRequest request, CancellationToken ct)
    {
        await service.AssignProjectsAsync(id, request.ProjectIds, ct);
        return NoContent();
    }
}
