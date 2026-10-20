// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Users;

/// <summary>
/// OmniPreferences' durable store (recette R1-66): every OE preference (appearance, grid layouts) and
/// every Aetheus preference goes to <c>api/users/me/preferences</c> for the signed-in user, and the
/// browser cache follows the server. Without a signed-in user (or for an identity without a user row)
/// the scope is <see cref="OmniPreferences.NoUserScope"/>: OmniPreferences then answers the defaults and
/// keeps what is written in memory, without calling the store. A call that still arrives without a user,
/// or for an account other than the one the cache was opened for, throws: OmniPreferences logs it and
/// keeps the value in its cache. A value over the server's bounds is refused here, before the request,
/// so it is never sent again and again.
/// </summary>
public sealed class ApiPreferenceStore(ApiClient api, PreferenceScope scope) : IOmniPreferenceStore
{
    public async Task<OmniPreference?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        RequireUser();
        return ToOmni(await api.Preferences.GetAsync(key, cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<OmniPreference>> GetChangedSinceAsync(DateTimeOffset? since, CancellationToken cancellationToken = default)
    {
        RequireUser();
        var changed = await api.Preferences.GetChangedSinceAsync(since, cancellationToken).ConfigureAwait(false);
        return changed.Select(preference => ToOmni(preference)!).ToList();
    }

    public Task SaveAsync(OmniPreference preference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preference);
        RequireUser();
        if (!UserPreferenceRules.IsValidKey(preference.Key))
            throw new ArgumentException($"The preference key '{preference.Key}' is not accepted by the server.", nameof(preference));
        if (preference.Value is { Length: > UserPreferenceRules.MaxValueLength })
            throw new ArgumentException($"The preference '{preference.Key}' is longer than {UserPreferenceRules.MaxValueLength} characters.", nameof(preference));
        return api.Preferences.SaveAsync(preference.Key,
            new SaveUserPreferenceRequest { Value = preference.Value, UpdatedAt = preference.UpdatedAt }, cancellationToken);
    }

    /// <summary>The signed-in user's scope, or <see cref="OmniPreferences.NoUserScope"/> while nobody with a user row is signed in.</summary>
    public ValueTask<string> GetScopeAsync(CancellationToken cancellationToken = default)
    {
        var current = scope.Current ?? OmniPreferences.NoUserScope;
        scope.MarkOpened(current);
        return ValueTask.FromResult(current);
    }

    // The cache opened for another user (an account changed and the preferences are not reset yet)
    // never meets this user's server values: neither side is read or written until the reset.
    private void RequireUser()
    {
        if (scope.OpenedForAnotherUser)
            throw new InvalidOperationException("The preference cache belongs to the previous user until the preferences are reset for the new one.");
        if (scope.Current is null)
            throw new InvalidOperationException("No signed-in user: the preferences stay in memory until one signs in.");
    }

    private static OmniPreference? ToOmni(UserPreferenceDto? preference) =>
        preference is null ? null : new OmniPreference(preference.Key, preference.Value, preference.UpdatedAt);
}
