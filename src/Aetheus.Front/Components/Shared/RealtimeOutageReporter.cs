// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Reports long realtime outages to the system logs once the connections are back, one report per incident.
/// The hubs of a tab share the network, the backend and the token renewal, so one outage brings them all
/// down and back together: reported one by one, the cut of run 2729 read as four errors of 404 s (recette
/// R1-89). Restorations that arrive within <see cref="GroupingWindow"/> of the first one are one incident:
/// one report names every hub with its own loss time and duration, so nothing distinct is lost. When the
/// page's visibility is tracked (<see cref="PageVisibilityTimeline"/>), the report also says how long the
/// page was hidden and how long visible during the outage, so a sleeping machine or a parked tab reads
/// differently from an outage seen on screen (recette R1-42).
/// </summary>
internal sealed class RealtimeOutageReporter(
    ClientErrorReporter reporter, TimeProvider clock, PageVisibilityTimeline? visibility = null)
{
    /// <summary>An outage at least this long is reported once the connection is back.</summary>
    internal static readonly TimeSpan ReportedOutage = TimeSpan.FromSeconds(60);

    /// <summary>How long after the first restoration the others are still counted in the same incident.</summary>
    internal static readonly TimeSpan GroupingWindow = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly List<RestoredOutage> _pending = [];
    private bool _flushScheduled;

    /// <summary>The clock the connections time their outages with.</summary>
    public TimeProvider Clock => clock;

    /// <summary>Records that <paramref name="hub"/>, down since <paramref name="since"/>, is connected again.</summary>
    public void Restored(string hub, DateTimeOffset since)
    {
        var now = clock.GetUtcNow();
        var seconds = (int)(now - since).TotalSeconds;
        if (seconds < ReportedOutage.TotalSeconds) return;
        // Measured now, while the hidden stretches of the outage are still known.
        int? hiddenSeconds = visibility is null
            ? null
            : Math.Min(seconds, (int)visibility.HiddenBetween(since, now).TotalSeconds);
        lock (_gate)
        {
            _pending.Add(new RestoredOutage(hub, since, seconds, hiddenSeconds));
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        _ = FlushLaterAsync();
    }

    private async Task FlushLaterAsync()
    {
        await Task.Delay(GroupingWindow, clock).ConfigureAwait(false);
        RestoredOutage[] incident;
        lock (_gate)
        {
            incident = [.. _pending];
            _pending.Clear();
            _flushScheduled = false;
        }
        if (incident.Length == 0) return;
        // Restored on its own: a warning, not an error (decision of 2026-10-14). A connection that
        // closes for good is reported as an error by HubConnectionFactory.
        reporter.Report(Guid.NewGuid().ToString("N"), Summary(incident), Describe(incident), level: ClientErrorLogLevels.Warning);
    }

    private static string Summary(RestoredOutage[] incident) =>
        incident.Length == 1
            ? "Realtime connection restored after an outage"
            : "Realtime connections restored after an outage";

    /// <summary>One sentence per distinct loss time and duration, naming the hubs that share it.</summary>
    internal static string Describe(IReadOnlyCollection<RestoredOutage> incident)
    {
        if (incident.Count == 1)
        {
            var only = incident.First();
            return string.Create(CultureInfo.InvariantCulture,
                $"The '{only.Hub}' realtime connection was lost at {only.Since:u} and came back after {only.Seconds} s.{VisibilitySentence(only)}");
        }
        var parts = incident
            .GroupBy(o => (Since: o.Since.ToString("u", CultureInfo.InvariantCulture), o.Seconds, o.HiddenSeconds))
            .Select(group => string.Create(CultureInfo.InvariantCulture,
                $"{string.Join(", ", group.Select(o => $"'{o.Hub}'"))} lost at {group.Key.Since}, back after {group.Key.Seconds} s{VisibilityClause(group.Key.Seconds, group.Key.HiddenSeconds)}"));
        return string.Create(CultureInfo.InvariantCulture,
            $"{incident.Count} realtime connections came back after one outage: {string.Join("; ", parts)}.");
    }

    private static string VisibilitySentence(RestoredOutage outage) =>
        outage.HiddenSeconds is { } hidden
            ? string.Create(CultureInfo.InvariantCulture,
                $" The page was hidden for {hidden} s and visible for {outage.Seconds - hidden} s during the outage.")
            : string.Empty;

    private static string VisibilityClause(int seconds, int? hiddenSeconds) =>
        hiddenSeconds is { } hidden
            ? string.Create(CultureInfo.InvariantCulture, $" (page hidden {hidden} s, visible {seconds - hidden} s)")
            : string.Empty;

    /// <summary>One restored hub; <paramref name="HiddenSeconds"/> is null when the page visibility is not tracked.</summary>
    internal sealed record RestoredOutage(string Hub, DateTimeOffset Since, int Seconds, int? HiddenSeconds = null);
}
