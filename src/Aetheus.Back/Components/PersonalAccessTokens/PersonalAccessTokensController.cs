// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Auth;

namespace Aetheus.Back.Components.PersonalAccessTokens;

/// <summary>
/// Manages the calling user's Personal Access Tokens (ADR-024 4.5). A PAT can never be used to mint
/// or revoke tokens (no token laundering): every action rejects a PAT-authenticated principal, so only
/// an interactive JWT session can manage tokens.
/// <para>Note: pinning to the JWT scheme alone is insufficient - the JWT bearer handler's
/// <c>ForwardDefaultSelector</c> forwards a PAT-shaped token to the PAT scheme even when the JWT scheme
/// is requested explicitly. The explicit <see cref="RejectPatPrincipal"/> guard is the real defense.</para>
/// </summary>
[ApiController]
[Route("api/personal-access-tokens")]
[Authorize]
public sealed class PersonalAccessTokensController(IPersonalAccessTokenService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<PersonalAccessTokenDto>>> GetMine(
        [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        if (RejectPatPrincipal(out var forbid)) return forbid;
        if (!TryGetUserId(out var userId))
            return BadRequest("Personal access tokens require a real user account.");
        return Ok(await service.GetForUserAsync(userId, request, ct));
    }

    [HttpPost]
    public async Task<ActionResult<CreatedPersonalAccessTokenDto>> Create(
        [FromBody] CreatePersonalAccessTokenRequest request, CancellationToken ct)
    {
        if (RejectPatPrincipal(out var forbid)) return forbid;
        if (!TryGetUserId(out var userId))
            return BadRequest("Personal access tokens require a real user account.");
        return Ok(await service.CreateAsync(userId, request, ct));
    }

    [HttpDelete("{id:int}")]
    [NotResourceScoped("A token belongs to the calling user: RevokeAsync only matches a token of their own user id.")]
    public async Task<IActionResult> Revoke(int id, CancellationToken ct)
    {
        if (RejectPatPrincipal(out var forbid)) return forbid;
        if (!TryGetUserId(out var userId))
            return BadRequest("Personal access tokens require a real user account.");
        var revoked = await service.RevokeAsync(id, userId, ct);
        return revoked ? NoContent() : NotFound();
    }

    /// <summary>Blocks a request that authenticated with a PAT (rather than an interactive JWT session):
    /// a token must never be able to create or revoke tokens.</summary>
    private bool RejectPatPrincipal(out ActionResult forbid)
    {
        if (PatPrincipalCheck.IsPatPrincipal(User))
        {
            forbid = StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Personal access tokens cannot manage tokens; use an interactive session." });
            return true;
        }
        forbid = null!;
        return false;
    }

    private bool TryGetUserId(out int userId)
        => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
