// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.TestDoubles;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that says when the code under test arms a timer on it. A loop that
/// waits <c>Task.Delay(interval, clock)</c> between passes arms one timer per pass, at the end of the
/// previous one: a test advances the clock once that timer is armed, and knows a pass is over when the next
/// one is, instead of advancing blindly and sleeping for the pass to happen.
/// </summary>
internal sealed class ObservedFakeClock(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly StateSignal _changes = new();
    private readonly List<(TimeSpan Due, ObservedTimer Timer)> _timers = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ObservedTimer? observed = null;
        var inner = base.CreateTimer(
            value =>
            {
                observed!.Fired = true;
                callback(value);
            },
            state, dueTime, period);
        observed = new ObservedTimer(inner, _changes)
        {
            Periodic = period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero
        };
        lock (_timers) _timers.Add((dueTime, observed));
        _changes.Pulse();
        return observed;
    }

    /// <summary>How many timers due after <paramref name="due"/> were armed so far.</summary>
    public int Armed(TimeSpan due)
    {
        lock (_timers) return _timers.Count(timer => timer.Due == due);
    }

    /// <summary>How many timers due after <paramref name="due"/> can still fire: not disposed, and not
    /// already fired for a one-shot timer.</summary>
    public int Pending(TimeSpan due)
    {
        lock (_timers) return _timers.Count(timer => timer.Due == due && !timer.Timer.Disposed
            && (timer.Timer.Periodic || !timer.Timer.Fired));
    }

    /// <summary>Completes once <paramref name="count"/> timers due after <paramref name="due"/> were armed.</summary>
    public Task WhenArmedAsync(TimeSpan due, int count) =>
        _changes.WhenAsync(() => Armed(due) >= count, $"{count} timer(s) of {due} armed");

    /// <summary>How many timers, whatever their due time, can still fire.</summary>
    public int PendingTimers
    {
        get { lock (_timers) return _timers.Count(timer => !timer.Timer.Disposed && (timer.Timer.Periodic || !timer.Timer.Fired)); }
    }

    /// <summary>Completes once <paramref name="condition"/> holds, re-checked whenever a timer of this clock
    /// is armed or disposed.</summary>
    public Task WhenAsync(Func<bool> condition, string what) => _changes.WhenAsync(condition, what);

    /// <summary>Completes once some timer is armed that moving the clock would fire.</summary>
    public Task WhenAnyPendingAsync() => _changes.WhenAsync(() => PendingTimers > 0, "a timer armed");

    /// <summary>Completes once no timer due after <paramref name="due"/> can fire any more.</summary>
    public Task WhenNonePendingAsync(TimeSpan due) =>
        _changes.WhenAsync(() => Pending(due) == 0, $"every timer of {due} stopped");

    private sealed class ObservedTimer(ITimer inner, StateSignal changes) : ITimer
    {
        public volatile bool Fired;
        public volatile bool Disposed;
        public bool Periodic { get; init; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

        public void Dispose()
        {
            Disposed = true;
            inner.Dispose();
            changes.Pulse();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
