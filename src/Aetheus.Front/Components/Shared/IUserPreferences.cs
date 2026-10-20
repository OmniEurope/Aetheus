// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The signed-in user's preferences (recette R1-66): kept on the server for that user and cached in the
/// browser, the server winning. Read and written by the pages for their own choices (favourites,
/// columns, views, sections opened); the keys are in <see cref="PreferenceKeys"/>.
/// </summary>
public interface IUserPreferences
{
    /// <summary>Raised with the key when a value changes on the server (another device), in another tab, or because another user signed in.</summary>
    event Func<string, Task>? Changed;

    /// <summary>The value of <paramref name="key"/>, or <c>default</c> when the user has none or nobody is signed in.</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);

    /// <summary>Stores <paramref name="value"/> for the signed-in user; kept in this page only while nobody is signed in.</summary>
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);

    /// <summary>Removes <paramref name="key"/> for the signed-in user, on every device.</summary>
    Task RemoveAsync(string key, CancellationToken ct = default);
}
