// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// <see cref="IUserPreferences"/> over OE's <see cref="OmniPreferences"/>, whose store is
/// <see cref="Users.ApiPreferenceStore"/>. Before sign-in OmniPreferences answers the defaults and keeps
/// what is written in memory only (<see cref="OmniPreferences.NoUserScope"/>); after a change of user
/// (<see cref="OmniPreferences.ResetUserAsync"/>) every key read in this page is raised again through
/// <see cref="Changed"/>, with the new user's value.
/// </summary>
public sealed class UserPreferences : IUserPreferences, IDisposable
{
    private readonly OmniPreferences _preferences;

    public UserPreferences(OmniPreferences preferences)
    {
        _preferences = preferences;
        _preferences.Changed += OnChangedAsync;
    }

    public event Func<string, Task>? Changed;

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) => _preferences.GetAsync<T>(key, ct);

    public Task SetAsync<T>(string key, T value, CancellationToken ct = default) => _preferences.SetAsync(key, value, ct);

    public Task RemoveAsync(string key, CancellationToken ct = default) => _preferences.RemoveAsync(key, ct);

    public void Dispose() => _preferences.Changed -= OnChangedAsync;

    private Task OnChangedAsync(OmniPreferenceChangedEventArgs change) =>
        change.Origin == OmniPreferenceChangeOrigin.Local || Changed is not { } handlers
            ? Task.CompletedTask
            : Task.WhenAll(handlers.GetInvocationList().Cast<Func<string, Task>>().Select(handler => handler(change.Key)));
}
