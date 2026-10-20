// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The child runs a user left open in run timelines (S-FEAT-EXST), a user preference since R1-66. One
/// key holds every run, so a long history of runs viewed does not grow into as many server rows: the
/// map keeps the <see cref="MaxRuns"/> runs changed last.
/// </summary>
internal sealed class RunTimelineExpansionStore(IUserPreferences preferences)
{
    internal const int MaxRuns = 50;

    public async Task<int[]?> LoadAsync(int runId)
    {
        var map = await preferences.GetAsync<Dictionary<string, int[]>>(PreferenceKeys.RunTimelineExpanded);
        return map?.GetValueOrDefault(runId.ToString(CultureInfo.InvariantCulture));
    }

    public async Task SaveAsync(int runId, IReadOnlyCollection<int> expanded)
    {
        var map = await preferences.GetAsync<Dictionary<string, int[]>>(PreferenceKeys.RunTimelineExpanded) ?? [];
        var key = runId.ToString(CultureInfo.InvariantCulture);
        // Removed then added again, so the run moves to the end: the oldest entries come first.
        map.Remove(key);
        if (expanded.Count > 0)
            map[key] = [.. expanded];
        foreach (var oldest in map.Keys.Take(Math.Max(0, map.Count - MaxRuns)).ToList())
            map.Remove(oldest);
        await preferences.SetAsync(PreferenceKeys.RunTimelineExpanded, map);
    }
}
