// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Remembers when the tab was hidden, so a realtime outage report can say how much of the outage the page
/// spent in the background (R1-42). A tab left open while the machine sleeps or the tab is parked loses
/// its connections without any server fault: "hidden for the whole outage" tells that from a real outage
/// seen on screen. Fed by the browser's visibility changes (<c>Aetheus.watchPageVisibility</c>); the page
/// is taken as visible until the first change is reported.
/// </summary>
internal sealed class PageVisibilityTimeline(TimeProvider clock)
{
    /// <summary>Hidden stretches kept, the oldest dropped first: enough for a day of ordinary use.</summary>
    internal const int MaxStretches = 256;

    private readonly object _gate = new();
    private readonly List<(DateTimeOffset From, DateTimeOffset To)> _hidden = [];
    private DateTimeOffset? _hiddenSince;

    /// <summary>The page left the foreground. Repeating it keeps the first instant.</summary>
    public void Hidden()
    {
        lock (_gate)
            _hiddenSince ??= clock.GetUtcNow();
    }

    /// <summary>The page is in the foreground again; closes the hidden stretch that was open, if any.</summary>
    public void Visible()
    {
        lock (_gate)
        {
            if (_hiddenSince is not { } from) return;
            _hiddenSince = null;
            _hidden.Add((from, clock.GetUtcNow()));
            if (_hidden.Count > MaxStretches)
                _hidden.RemoveRange(0, _hidden.Count - MaxStretches);
        }
    }

    /// <summary>How long the page was hidden within <paramref name="from"/> to <paramref name="to"/>.</summary>
    public TimeSpan HiddenBetween(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) return TimeSpan.Zero;
        lock (_gate)
        {
            var total = TimeSpan.Zero;
            foreach (var (start, end) in _hidden)
                total += Overlap(start, end, from, to);
            if (_hiddenSince is { } open)
                total += Overlap(open, to, from, to);
            return total;
        }
    }

    private static TimeSpan Overlap(DateTimeOffset start, DateTimeOffset end, DateTimeOffset from, DateTimeOffset to)
    {
        var begin = start > from ? start : from;
        var finish = end < to ? end : to;
        return finish > begin ? finish - begin : TimeSpan.Zero;
    }
}
