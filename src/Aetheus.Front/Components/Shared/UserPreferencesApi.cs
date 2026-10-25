// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The signed-in user's preferences on the server (<c>api/users/me/preferences</c>, recette R1-66). Unlike
/// the other sub-clients, a refused request throws: the preference store reports it to OmniPreferences,
/// which keeps the value in the browser and writes it again at the next synchronization.
/// </summary>
public sealed class UserPreferencesApi(HttpClient http) : ApiClientBase(http)
{
    private const string Root = "api/users/me/preferences";

    /// <summary>The preferences changed at or after <paramref name="since"/> (all of them when null), removed ones included.</summary>
    public async Task<List<UserPreferenceDto>> GetChangedSinceAsync(DateTimeOffset? since, CancellationToken ct = default)
    {
        var url = since is { } from
            ? $"{Root}?since={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}"
            : Root;
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<UserPreferenceDto>>(JsonOptions.Web, ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>One preference, its tombstone, or null when the user never had it.</summary>
    public async Task<UserPreferenceDto?> GetAsync(string key, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"{Root}/{Uri.EscapeDataString(key)}", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response.StatusCode == HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync<UserPreferenceDto>(JsonOptions.Web, ct).ConfigureAwait(false);
    }

    /// <summary>Writes one preference; a null value is a removal, kept as a tombstone with its date.</summary>
    public async Task SaveAsync(string key, SaveUserPreferenceRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"{Root}/{Uri.EscapeDataString(key)}")
        {
            Content = JsonContent.Create(request, options: JsonOptions.Web)
        };
        // The store checks the bounds before sending; a 400 left is reported by the store, not as a toast
        // repeated at every synchronization.
        message.Options.Set(ErrorNotificationHandler.CallerHandlesBadRequest, true);
        using var response = await Http.SendAsync(message, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
