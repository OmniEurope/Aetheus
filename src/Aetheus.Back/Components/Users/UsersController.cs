// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Back.Components.Users;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController(IUserService service) : ControllerBase
{
    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<PaginatedResult<UserDto>>> GetUsers(
        [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        return Ok(await service.GetUsersAsync(request, ct));
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

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<UserDto>> CreateUser([FromBody] CreateUserRequest request, CancellationToken ct)
    {
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
