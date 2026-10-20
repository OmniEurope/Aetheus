// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Auth;

/// <summary>
/// How long a revoked refresh token row is kept. The row is what lets a reused (stolen) token be
/// detected and the concurrent-refresh race be absorbed, so reuse detection works inside this window
/// only; past it the row is deleted and the token reads as unknown.
/// </summary>
public static class RefreshTokenRetention
{
    /// <summary>A revoked token is replayable (grace) and detectable (theft) for this long, then deleted.</summary>
    public static readonly TimeSpan RevokedRetention = TimeSpan.FromMinutes(5);

    /// <summary>How often the background sweep deletes the revoked and expired rows.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>Revoked tokens whose <c>RevokedAt</c> is strictly before this instant are deletable.</summary>
    public static DateTime RevokedCutoff(DateTime utcNow) => utcNow - RevokedRetention;
}
