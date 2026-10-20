// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Front.Components.Users;

/// <summary>
/// The partition of the browser cache of OmniPreferences that belongs to the signed-in user: a hash of
/// the user id, so the storage keys carry no personal data. It remembers the scope last handed to
/// OmniPreferences, so the store never answers for one account with the cache of another: between a
/// change of account and <see cref="OmniPreferences.ResetUserAsync"/> (which asks for the scope again),
/// the store refuses every call.
/// </summary>
public sealed class PreferenceScope(AuthStateProvider auth)
{
    private string? _opened;

    /// <summary>The scope of the signed-in user, or null without a user row.</summary>
    public string? Current => auth.IsAuthenticated && auth.UserId is { } userId ? For(userId) : null;

    /// <summary>
    /// True when the scope last handed to OmniPreferences is not the signed-in user's (an account changed
    /// and the preferences have not been reset for it yet).
    /// </summary>
    public bool OpenedForAnotherUser =>
        _opened is not null && !string.Equals(_opened, Current ?? OmniPreferences.NoUserScope, StringComparison.Ordinal);

    /// <summary>Records the scope handed to OmniPreferences (<see cref="OmniPreferences.NoUserScope"/> without a user).</summary>
    internal void MarkOpened(string scope) => _opened = scope;

    /// <summary>A stable, opaque value per user id, without a colon (OmniPreferences' storage key separator).</summary>
    internal static string For(int userId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("aetheus-preferences:" + userId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return "u" + Convert.ToHexStringLower(hash.AsSpan(0, 12));
    }
}
