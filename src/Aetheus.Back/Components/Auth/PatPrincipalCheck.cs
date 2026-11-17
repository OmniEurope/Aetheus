// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;

namespace Aetheus.Back.Components.Auth;

/// <summary>
/// R-54: the one answer to "did this request authenticate with a personal access token?". Every place that
/// refuses a PAT (token management, session renewal) asks here, so the rule cannot drift between them.
/// </summary>
public static class PatPrincipalCheck
{
    /// <summary>True when an identity came from the PAT scheme, or the principal carries a PAT id or scope
    /// claim (the Git and package-registry handlers resolve PATs under their own scheme names).</summary>
    public static bool IsPatPrincipal(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Identities.Any(identity => identity.AuthenticationType == PatConstants.SchemeName)
            || user.HasClaim(claim => claim.Type is PatConstants.TokenIdClaimType or PatConstants.ScopeClaimType);
    }
}
