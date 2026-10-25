// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;

namespace Aetheus.Back.Services;

/// <summary>
/// Remembers which background iterations are running or have just finished, so a slow request can be
/// matched with the periodic work that overlapped it (recette R1-40: pauses of 2 s about every five
/// minutes, cause unknown). An iteration announces itself with <see cref="Begin"/> and ends it by
/// disposing the result. Only the iterations that call it are known: a service that does not is invisible
/// here, so an empty answer means "none of the tracked ones", not "no background work".
/// </summary>
internal sealed class BackgroundWorkTracker(TimeProvider time)
{
    /// <summary>The tracker the background loops report to and the diagnostics read.</summary>
    public static BackgroundWorkTracker Shared { get; } = new(TimeProvider.System);

    private sealed class Entry
    {
        public int Running;
        public long LastEndedTicks;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Marks the start of one iteration of <paramref name="name"/>; dispose the result when it ends.</summary>
    public IDisposable Begin(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var entry = _entries.GetOrAdd(name, static _ => new Entry());
        Interlocked.Increment(ref entry.Running);
        return new Iteration(() => End(entry));
    }

    /// <summary>
    /// The tracked iterations still running, or ended at or after <paramref name="since"/>, sorted by
    /// name: "Name (running)" or "Name (ended)". Empty when none overlapped.
    /// </summary>
    public IReadOnlyList<string> Overlapping(DateTimeOffset since)
    {
        var sinceTicks = since.UtcTicks;
        List<string> names = [];
        foreach (var (name, entry) in _entries)
        {
            if (Volatile.Read(ref entry.Running) > 0)
                names.Add($"{name} (running)");
            else if (Volatile.Read(ref entry.LastEndedTicks) >= sinceTicks)
                names.Add($"{name} (ended)");
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private void End(Entry entry)
    {
        Volatile.Write(ref entry.LastEndedTicks, time.GetUtcNow().UtcTicks);
        Interlocked.Decrement(ref entry.Running);
    }

    private sealed class Iteration(Action end) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
                end();
        }
    }
}
