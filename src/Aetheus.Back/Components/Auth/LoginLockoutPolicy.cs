// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Auth;

/// <summary>
/// The account lockout of the sign-in (F-011), shared by every path that checks the password of an account:
/// login, Git Basic-auth and the 2FA disabling (R-50). One failed-attempt counter, one lockout window.
/// </summary>
internal static class LoginLockoutPolicy
{
    internal const int MaxFailedAttempts = 5;
    private static readonly TimeSpan BaseLockoutDuration = TimeSpan.FromMinutes(15);

    internal static bool IsLockedOut(User user, DateTime nowUtc)
        => user.LockoutEndUtc is { } end && end > nowUtc;

    /// <summary>Counts one failure and locks the account once the threshold is reached.</summary>
    internal static async Task RecordFailureAsync(IAuthRepository repo, TimeProvider timeProvider, int userId, CancellationToken ct)
    {
        // The stored count, not the snapshot + 1: concurrent failures read the same snapshot.
        var newCount = await repo.IncrementFailedLoginAsync(userId, ct).ConfigureAwait(false);
        if (newCount < MaxFailedAttempts) return;

        // Exponential backoff: 15 min × 2^(lockout_round - 1), capped at 24 h
        var lockoutRound = newCount / MaxFailedAttempts;
        var duration = TimeSpan.FromTicks(
            Math.Min(BaseLockoutDuration.Ticks * (1L << Math.Min(lockoutRound - 1, 6)),
                      TimeSpan.FromHours(24).Ticks));
        var lockoutEnd = timeProvider.GetUtcNow().UtcDateTime.Add(duration);
        await repo.LockoutUserAsync(userId, lockoutEnd, ct).ConfigureAwait(false);
    }
}
