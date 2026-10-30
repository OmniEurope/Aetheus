// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using Aetheus.Back.Components.Organizations;

namespace Aetheus.Back.Components.Auth;

/// <summary>
/// The roles. A site Admin administers every role. An organization's administrator (org.roles.manage on it,
/// Administration section, owner decision 2026-10-28) reads the built-in roles and administers its
/// organization's own roles only: a role of another organization is answered 404, a built-in role or a role
/// of every organization 403 on any change, and it never gives the site Admin role.
/// </summary>
[ApiController]
[Route("api/roles")]
[Authorize]
public class RolesController(IRoleService roleService, IResourceAuthorizationService authz, IOrganizationService organizations) : ControllerBase
{
    /// <summary>What the caller may do with one role.</summary>
    private enum RoleAccess
    {
        /// <summary>The caller administers no organization: 403 on every role, whichever it names.</summary>
        NoAdministration,
        /// <summary>Not the caller's: answered 404, so a role of another organization is not even confirmed.</summary>
        None,
        /// <summary>A built-in role, read by an organization's administrator.</summary>
        Read,
        /// <summary>Changed by the caller: any role for a site Admin, its organization's own for an organization's administrator.</summary>
        Manage
    }

    private sealed record RoleAccessResult(RoleDto? Role, RoleAccess Access, IReadOnlyList<int>? Organizations);

    private bool IsSiteAdmin => User.IsInRole(RoleNames.Admin);

    [HttpGet]
    public async Task<ActionResult<PaginatedResult<RoleDto>>> GetRoles(
        [FromQuery] PaginationRequest request, [FromQuery, Range(1, int.MaxValue)] int? organizationId, CancellationToken ct)
    {
        var scope = await AdministrationScope.ResolveAsync(authz, User, PermissionKeys.OrganizationRolesManage, organizationId, ct);
        if (scope is null) return Forbid();
        if (scope.OrganizationIds is not { } organizationIds)
            return Ok(await roleService.GetRolesAsync(request, ct));
        // A site Admin filtering one organization also sees the roles of every organization; an organization's
        // administrator, the built-in ones only.
        return Ok(await roleService.GetRolesAsync(request, organizationIds, IsSiteAdmin ? null : RoleNames.BuiltIn, ct));
    }

    /// <summary>
    /// The roles a holder of org.members.manage on <paramref name="organizationId"/> gives there when it creates
    /// an account (R-10 d): the built-in roles held for one organization and that organization's own roles, never
    /// Admin, a role of every organization or another organization's. Needs no org.roles.manage; 403 elsewhere.
    /// </summary>
    [HttpGet("assignable")]
    public async Task<ActionResult<PaginatedResult<RoleDto>>> GetAssignableRoles(
        [FromQuery] PaginationRequest request, [FromQuery, Range(1, int.MaxValue)] int organizationId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.OrganizationMembersManage, organizationId, ct)) return Forbid();
        return Ok(await roleService.GetRolesAsync(request, [organizationId], RoleNames.BuiltInForOneOrganization, ct));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<RoleDto>> GetRole(int id, CancellationToken ct)
    {
        var access = await ResolveRoleAccessAsync(id, ct);
        if (RefuseUnlessReadable(access) is { } refused) return refused;
        return Ok(access.Role);
    }

    [HttpPost]
    public async Task<ActionResult<RoleDto>> CreateRole([FromBody] CreateRoleRequest request, CancellationToken ct)
    {
        // An organization's administrator makes roles of its organization only, never one of every organization.
        if (!IsSiteAdmin && (request.OrganizationId is not { } organizationId
            || !await authz.HasPermissionAsync(User, PermissionKeys.OrganizationRolesManage, organizationId, ct)))
            return Forbid();
        var role = await roleService.CreateRoleAsync(request, ct);
        return CreatedAtAction(nameof(GetRole), new { id = role.Id }, role);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<RoleDto>> UpdateRole(int id, [FromBody] UpdateRoleRequest request, CancellationToken ct)
    {
        if (RefuseUnlessManaged(await ResolveRoleAccessAsync(id, ct)) is { } refused) return refused;
        var role = await roleService.UpdateRoleAsync(id, request, ct);
        if (role is null) return NotFound();
        return Ok(role);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteRole(int id, CancellationToken ct)
    {
        if (RefuseUnlessManaged(await ResolveRoleAccessAsync(id, ct)) is { } refused) return refused;
        var deleted = await roleService.DeleteRoleAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    [HttpGet("{id:int}/permissions")]
    public async Task<ActionResult<List<ResourcePermissionDto>>> GetPermissions(int id, CancellationToken ct)
    {
        if (RefuseUnlessReadable(await ResolveRoleAccessAsync(id, ct)) is { } refused) return refused;
        return Ok(await roleService.GetPermissionsForRoleAsync(id, ct));
    }

    [HttpPut("{id:int}/permissions")]
    public async Task<IActionResult> SetPermissions(int id, [FromBody] SetResourcePermissionsRequest request, CancellationToken ct)
    {
        if (RefuseUnlessManaged(await ResolveRoleAccessAsync(id, ct)) is { } refused) return refused;
        await roleService.SetPermissionsForRoleAsync(id, request, ct);
        return NoContent();
    }

    /// <summary>R-11: the named permissions the role holds on its whole organization.</summary>
    [HttpGet("{id:int}/permission-keys")]
    public async Task<ActionResult<List<string>>> GetPermissionKeys(int id, CancellationToken ct)
    {
        if (RefuseUnlessReadable(await ResolveRoleAccessAsync(id, ct)) is { } refused) return refused;
        return Ok(await roleService.GetPermissionKeysForRoleAsync(id, ct));
    }

    /// <summary>R-11: replaces the named permissions the role holds on its whole organization.</summary>
    [HttpPut("{id:int}/permission-keys")]
    public async Task<IActionResult> SetPermissionKeys(int id, [FromBody] SetRolePermissionKeysRequest request, CancellationToken ct)
    {
        if (RefuseUnlessManaged(await ResolveRoleAccessAsync(id, ct)) is { } refused) return refused;
        await roleService.SetPermissionKeysForRoleAsync(id, request, ct);
        return NoContent();
    }

    [HttpPost("{id:int}/clone")]
    public async Task<ActionResult<RoleDto>> CloneRole(int id, CancellationToken ct)
    {
        if (RefuseUnlessManaged(await ResolveRoleAccessAsync(id, ct)) is { } refused) return refused;
        var role = await roleService.CloneRoleAsync(id, ct);
        if (role is null) return NotFound();
        return CreatedAtAction(nameof(GetRole), new { id = role.Id }, role);
    }

    /// <summary>The holders of the role; for an organization's administrator, those of its organizations only.</summary>
    [HttpGet("{id:int}/users")]
    public async Task<ActionResult<PaginatedResult<RoleUserDto>>> GetUsersInRole(
        int id, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        var access = await ResolveRoleAccessAsync(id, ct);
        if (RefuseUnlessReadable(access) is { } refused) return refused;
        return Ok(access.Organizations is { } scope
            ? await roleService.GetUsersInRoleAsync(id, request, scope, ct)
            : await roleService.GetUsersInRoleAsync(id, request, ct));
    }

    /// <summary>The users the role may be given to; for an organization's administrator, the members of the
    /// role's organization.</summary>
    [HttpGet("{id:int}/available-users")]
    public async Task<ActionResult<PaginatedResult<RoleUserDto>>> GetUsersAvailableForRole(
        int id, [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        var access = await ResolveRoleAccessAsync(id, ct);
        if (RefuseUnlessManaged(access) is { } refused) return refused;
        return Ok(IsSiteAdmin
            ? await roleService.GetUsersAvailableForRoleAsync(id, request, ct)
            : await roleService.GetUsersAvailableForRoleAsync(id, request, [access.Role!.OrganizationId!.Value], ct));
    }

    [HttpPost("{id:int}/users")]
    public async Task<IActionResult> AddUserToRole(int id, [FromBody] AddUserToRoleRequest request, CancellationToken ct)
    {
        var access = await ResolveRoleAccessAsync(id, ct);
        if (RefuseUnlessManaged(access) is { } refused) return refused;
        // An organization's administrator gives its organization's role, for it, to one of its members: never
        // another organization, never a user it would enrol by walking the global user ids (S2).
        if (!IsSiteAdmin && (request.OrganizationId != access.Role!.OrganizationId
            || !await IsMemberAsync(request.UserId, request.OrganizationId!.Value, ct)))
            return NotFound();
        await roleService.AddUserToRoleAsync(id, request.UserId, request.OrganizationId, ct);
        return NoContent();
    }

    [HttpDelete("{id:int}/users/{userId:int}")]
    public async Task<IActionResult> RemoveUserFromRole(int id, int userId, [FromQuery] int? organizationId, CancellationToken ct)
    {
        var access = await ResolveRoleAccessAsync(id, ct);
        if (RefuseUnlessManaged(access) is { } refused) return refused;
        if (!IsSiteAdmin && organizationId != access.Role!.OrganizationId) return NotFound();
        await roleService.RemoveUserFromRoleAsync(id, userId, organizationId, ct);
        return NoContent();
    }

    [HttpGet("~/api/users/{userId:int}/effective-permissions")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<UserPermissionSummaryDto>> GetEffectivePermissions(int userId, CancellationToken ct)
    {
        var summary = await roleService.GetEffectivePermissionsAsync(userId, ct);
        if (summary is null) return NotFound();
        return Ok(summary);
    }

    // Any authenticated user reads their OWN effective permissions (drives permission-gated UI).
    // Only [Authorize] here - must NOT inherit an Admin role requirement, or every non-admin
    // strands their permission-gated buttons as disabled. See UsersController for the same pattern.
    [HttpGet("~/api/users/me/permissions")]
    public async Task<ActionResult<UserPermissionSummaryDto>> GetMyPermissions(CancellationToken ct)
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return Unauthorized();
        var summary = await roleService.GetMyPermissionsAsync(User, ct);
        if (summary is null) return NotFound();
        return Ok(summary);
    }

    /// <summary>
    /// The caller's access to role <paramref name="id"/>. A site Admin manages every role. An organization's
    /// administrator manages the roles of the organizations where it holds org.roles.manage, reads the built-in
    /// roles (holders counted in its organizations), and does not see any other role.
    /// </summary>
    private async Task<RoleAccessResult> ResolveRoleAccessAsync(int id, CancellationToken ct)
    {
        var administered = await authz.GetOrganizationsWithPermissionKeyAsync(User, PermissionKeys.OrganizationRolesManage, ct);
        if (administered is null)
        {
            var any = await roleService.GetRoleAsync(id, ct);
            return new RoleAccessResult(any, any is null ? RoleAccess.None : RoleAccess.Manage, null);
        }
        if (administered.Count == 0)
            return new RoleAccessResult(null, RoleAccess.NoAdministration, administered);

        var role = await roleService.GetRoleAsync(id, administered, ct);
        var access = role switch
        {
            { OrganizationId: { } owner } when administered.Contains(owner) => RoleAccess.Manage,
            { OrganizationId: null } when RoleNames.BuiltIn.Contains(role.Name, StringComparer.Ordinal) => RoleAccess.Read,
            _ => RoleAccess.None
        };
        return new RoleAccessResult(access == RoleAccess.None ? null : role, access, administered);
    }

    /// <summary>Null when the caller manages the role; else 403 for a role it only reads (or a caller administering
    /// nothing), 404 for any other.</summary>
    private static ActionResult? RefuseUnlessManaged(RoleAccessResult access) => access.Access switch
    {
        RoleAccess.Manage => null,
        RoleAccess.Read or RoleAccess.NoAdministration => new ForbidResult(),
        _ => new NotFoundResult()
    };

    /// <summary>Null when the caller reads the role; else 403 for a caller administering nothing, 404 for a
    /// role that is not its organizations'.</summary>
    private static ActionResult? RefuseUnlessReadable(RoleAccessResult access) => access.Access switch
    {
        RoleAccess.Manage or RoleAccess.Read => null,
        RoleAccess.NoAdministration => new ForbidResult(),
        _ => new NotFoundResult()
    };

    private async Task<bool> IsMemberAsync(int userId, int organizationId, CancellationToken ct) =>
        (await organizations.GetOrganizationsForUserAsync(userId, ct)).Any(organization => organization.OrganizationId == organizationId);
}
