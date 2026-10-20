// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;

namespace Aetheus.Front.Components.Settings;

/// <summary>
/// R1-66: copies, once per browser, the preferences this browser kept before they moved to the server
/// into the account of the first user who signs in here. A key the server already holds for that user
/// (a value or a removal) is left alone: the server wins. The copy is marked done in this browser, so a
/// second account signing in on the same browser does not receive the first one's choices. The old
/// browser keys are left in place, unread.
/// </summary>
public sealed class LegacyPreferenceMigration(IUserPreferences preferences, ApiClient api, IJSRuntime js)
{
    internal const string DoneMarker = AppConstants.LocalStoragePrefix + "preferences_migrated";

    private const string FavoritesKey = "aetheus.projects.favorites";
    private const string ViewModeKey = "aetheus.projects.view-mode";
    private const string ActiveOrganizationKey = "aetheus.activeOrgId";
    private const string ServerColumnsKey = "servers-columns-hidden";
    private const string PipelineViewPrefix = "aetheus.pipelines.view-tab.";
    private const string UnmetPrefix = "aetheus.unmet-dismissed.";
    private const string VisualLayoutPrefix = "vp-layout-";
    private const string RunTimelinePrefix = "run-tl-exp-";

    private static readonly string[] Prefixes =
        [FavoritesKey, ViewModeKey, ActiveOrganizationKey, ServerColumnsKey, PipelineViewPrefix, UnmetPrefix, VisualLayoutPrefix, RunTimelinePrefix];

    /// <summary>Copies what this browser holds and the server lacks; returns the number of keys copied, or null when already done here.</summary>
    public async Task<int?> RunAsync(string? username, CancellationToken ct = default)
    {
        if (await js.InvokeAsync<string?>("Aetheus.getLocal", ct, DoneMarker) is not null)
            return null;

        var held = (await api.Preferences.GetChangedSinceAsync(null, ct)).Select(preference => preference.Key).ToHashSet(StringComparer.Ordinal);
        var local = await js.InvokeAsync<Dictionary<string, string?>>("Aetheus.localEntries", ct, (object)Prefixes) ?? [];
        var copied = 0;
        foreach (var (key, value) in Plan(local, username).Where(entry => !held.Contains(entry.Key)))
        {
            await preferences.SetAsync(key, value, ct);
            copied++;
        }

        copied += await CopyLanguageAsync(held, ct);
        await js.InvokeVoidAsync("Aetheus.setLocal", ct, DoneMarker, "1");
        return copied;
    }

    /// <summary>The language, which stays in the browser as the boot cache (the appearance is
    /// <see cref="LegacyAppearanceMigration"/>'s).</summary>
    private async Task<int> CopyLanguageAsync(HashSet<string> held, CancellationToken ct)
    {
        var copied = 0;
        var language = await js.InvokeAsync<string?>("localStorage.getItem", ct, StorageKeys.Lang);
        if (!held.Contains(PreferenceKeys.Language) && !string.IsNullOrWhiteSpace(language))
        {
            await preferences.SetAsync(PreferenceKeys.Language, language.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "fr-FR" : "en", ct);
            copied++;
        }

        return copied;
    }

    /// <summary>The server keys and values for the browser entries; an entry that no longer reads is skipped.</summary>
    internal static IReadOnlyList<KeyValuePair<string, JsonElement>> Plan(IReadOnlyDictionary<string, string?> local, string? username)
    {
        var plan = new List<KeyValuePair<string, JsonElement>>();
        var runs = new SortedDictionary<int, JsonElement>();
        foreach (var (key, value) in local)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (key.StartsWith(RunTimelinePrefix, StringComparison.Ordinal))
            {
                if (TryId(key, RunTimelinePrefix, out var runId) && TryJson(value) is { ValueKind: JsonValueKind.Array } expanded)
                    runs[runId] = expanded;
                continue;
            }

            if (Target(key, value, username) is { } entry)
                plan.Add(entry);
        }

        if (runs.Count > 0)
            plan.Add(new(PreferenceKeys.RunTimelineExpanded, JsonSerializer.SerializeToElement(
                runs.TakeLast(RunTimelineExpansionStore.MaxRuns).ToDictionary(run => run.Key.ToString(CultureInfo.InvariantCulture), run => run.Value))));
        return plan;
    }

    private static KeyValuePair<string, JsonElement>? Target(string key, string value, string? username) => key switch
    {
        FavoritesKey => Json(PreferenceKeys.ProjectFavorites, TryJson(value)),
        ViewModeKey => Text(PreferenceKeys.ProjectViewMode, value),
        ServerColumnsKey => Text(PreferenceKeys.ServerHiddenColumns, value),
        ActiveOrganizationKey => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var organizationId)
            ? new(PreferenceKeys.ActiveOrganization, JsonSerializer.SerializeToElement(organizationId))
            : null,
        _ when username is not null && key == PipelineViewPrefix + username => Text(PreferenceKeys.PipelineListView, value),
        _ when TryId(key, UnmetPrefix, out var projectId) => Text(PreferenceKeys.UnmetDismissed(projectId), value),
        _ when TryId(key, VisualLayoutPrefix, out var pipelineId) => Json(PreferenceKeys.VisualLayout(pipelineId), TryJson(value)),
        _ => null
    };

    private static KeyValuePair<string, JsonElement> Text(string key, string value) => new(key, JsonSerializer.SerializeToElement(value));

    private static KeyValuePair<string, JsonElement>? Json(string key, JsonElement? value) => value is { } element ? new(key, element) : null;

    private static bool TryId(string key, string prefix, out int id)
    {
        id = 0;
        return key.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    private static JsonElement? TryJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Mandatory: a value this browser wrote by hand or truncated is not worth copying.
            return null;
        }
    }
}
