// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;

namespace Aetheus.Back.Components.Users;

/// <summary>
/// The signed-in user's preferences (recette R1-66), synchronized with the browser cache of
/// OmniPreferences. Every action works on the caller's own user id, taken from the token; the route
/// carries no user, so no request can read or write another user's preferences. An identity without a
/// user row (the bootstrap administrator) reads nothing and may not write.
/// </summary>
[ApiController]
[Route("api/users/me/preferences")]
[Authorize]
public sealed class UserPreferencesController(IUserPreferenceService service) : ControllerBase
{
    /// <summary>Every preference of the caller changed at or after <paramref name="since"/> (all when absent), removed ones included.</summary>
    [HttpGet]
    public async Task<ActionResult<List<UserPreferenceDto>>> GetAll([FromQuery] DateTimeOffset? since, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Ok(new List<UserPreferenceDto>());
        return Ok(await service.GetAllAsync(userId, since, ct));
    }

    /// <summary>One preference; 204 when the caller never had it.</summary>
    [HttpGet("{key}")]
    public async Task<ActionResult<UserPreferenceDto>> Get(string key, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return NoContent();
        var preference = await service.GetAsync(userId, key, ct);
        return preference is null ? NoContent() : Ok(preference);
    }

    [HttpPut("{key}")]
    public async Task<ActionResult<UserPreferenceDto>> Save(string key, [FromBody] SaveUserPreferenceRequest request, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Forbid();
        return Ok(await service.SaveAsync(userId, key, request, ct));
    }

    [HttpDelete("{key}")]
    public async Task<IActionResult> Remove(string key, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Forbid();
        await service.RemoveAsync(userId, key, ct);
        return NoContent();
    }

    private int? CurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}
