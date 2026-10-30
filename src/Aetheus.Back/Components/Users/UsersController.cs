// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Back.Components.Users;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController(
    IUserService service, IResourceAuthorizationService authz, IOrganizationRoleAssignmentService roleAssignments) : ControllerBase
{
    /// <summary>
    /// Every user for a site Admin; for an organization's administrator, the members of the organizations
    /// where it holds org.members.manage (Administration section, owner decision 2026-10-28), without their
    /// e-mails. <paramref name="organizationId"/> is the organization switcher's choice: one the caller
    /// administers, else 403.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<UserDto>>> GetUsers(
        [FromQuery] PaginationRequest request, [FromQuery, Range(1, int.MaxValue)] int? organizationId, CancellationToken ct)
    {
        var scope = await AdministrationScope.ResolveAsync(authz, User, PermissionKeys.OrganizationMembersManage, organizationId, ct);
        if (scope is null) return Forbid();
        return Ok(scope.OrganizationIds is { } organizations
            ? await service.GetOrganizationUsersAsync(request, organizations, User.IsInRole(RoleNames.Admin), ct)
            : await service.GetUsersAsync(request, ct));
    }

    [HttpGet("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<UserDto>> GetUser(int id, CancellationToken ct)
    {
        var user = await service.GetUserDetailAsync(id, ct);
        if (user is null) return NotFound();
        return Ok(user);
    }

    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> GetCurrentUser(CancellationToken ct)
    {
        var user = await service.GetCurrentUserAsync(ct);
        if (user is null) return NotFound();
        return Ok(user);
    }

    /// <summary>
    /// A site Admin creates any account. A holder of org.members.manage (R-10 d) creates one in the
    /// organizations where it holds that key only, with the built-in roles held for one organization and those
    /// organizations' own roles: a site-wide role, another organization, the Admin role, a role of every
    /// organization or of another organization are refused (403), an account in no organization too (400).
    /// Every refusal comes before the e-mail is looked at. An e-mail or username already used answers the same
    /// 409 as for the site Admin, naming nothing of the account holding it: no more than adding a member by
    /// exact login already tells.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<UserDto>> CreateUser([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var administered = await authz.GetOrganizationsWithPermissionKeyAsync(User, PermissionKeys.OrganizationMembersManage, ct);
        if (administered is not null)
        {
            if (administered.Count == 0 || request.Roles.Count > 0) return Forbid();
            if (request.OrganizationRoles.Count == 0)
                return BadRequest("An Organization Admin creates the account in one of the organizations it administers.");
            if (request.OrganizationRoles.Any(held => !administered.Contains(held.OrganizationId))
                || !await roleAssignments.AreGivenByOrganizationAdministratorAsync(request.OrganizationRoles, ct))
                return Forbid();
        }
        var user = await service.CreateUserAsync(request, ct);
        return CreatedAtAction(nameof(GetUser), new { id = user.Id }, user);
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<UserDto>> UpdateUser(int id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var user = await service.UpdateUserAsync(id, request, ct);
        if (user is null) return NotFound();
        return Ok(user);
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteUser(
        int id, CancellationToken ct,
        [FromQuery, StringLength(50)] string? transferPipelinesTo = null,
        [FromQuery] bool force = false)
    {
        var deleted = await service.DeleteUserAsync(id, ct, transferPipelinesTo, force);
        if (!deleted) return NotFound();
        return NoContent();
    }

    [HttpPost("{id:int}/change-password")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ChangePassword(int id, [FromBody] ChangeUserPasswordRequest request, CancellationToken ct)
    {
        var changed = await service.ChangePasswordAsync(id, request, ct);
        if (!changed) return NotFound();
        return NoContent();
    }

    // Self-service: any authenticated user changes their OWN password. The route segment
    // "me" cannot match the {id:int} constraint above, so the two never collide. The service
    // enforces the current-password check; a wrong/empty current password yields false → 400.
    [HttpPost("me/change-password")]
    public async Task<IActionResult> ChangeOwnPassword([FromBody] ChangeUserPasswordRequest request, CancellationToken ct)
    {
        var changed = await service.ChangeOwnPasswordAsync(request, ct);
        if (!changed) return BadRequest();
        return NoContent();
    }

    [HttpGet("roles")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<string>>> GetRoles(CancellationToken ct)
    {
        return Ok(await service.GetRolesAsync(ct));
    }
}
