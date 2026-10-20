// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Shows foreground catch-up while hubs reconnect and refetch, including work starting
/// within SettleWindow after a tab wakes. Hides after the final catch-up or SafetyTimeout; timers
/// bound display only and never fetch data.</summary>
public sealed class RealtimeSyncStatus(TimeProvider timeProvider) : IDisposable
{
    /// <summary>How long after the page is visible again a starting catch-up still counts as the return's.</summary>
    internal static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(5);

    /// <summary>The longest the indicator stays up, even if a catch-up never reports its end.</summary>
    internal static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(20);

    private enum Phase { Idle, Settling, Syncing }

    private readonly object _gate = new();
    private int _inFlight;
    private bool _hidden;
    private Phase _phase;
    private ITimer? _timer;
    private int _generation;

    /// <summary>Raised when <see cref="IsSyncing"/> changes, from whichever thread caused it.</summary>
    public event Action? Changed;

    /// <summary>True while the page, back in the foreground, waits for its realtime catch-up.</summary>
    public bool IsSyncing
    {
        get { lock (_gate) return _phase == Phase.Syncing; }
    }

    /// <summary>Catch-ups currently running, whether or not the page is watching.</summary>
    internal int InFlight
    {
        get { lock (_gate) return _inFlight; }
    }

    /// <summary>
    /// Marks the start of a reconnect and its refetch. Dispose the result once the data is current
    /// again, or once the attempt is abandoned; disposing twice counts once.
    /// </summary>
    public IDisposable BeginCatchUp()
    {
        bool changed;
        lock (_gate)
        {
            _inFlight++;
            changed = _phase == Phase.Settling && Enter(Phase.Syncing, SafetyTimeout);
        }
        if (changed) Changed?.Invoke();
        return new CatchUp(this);
    }

    /// <summary>The page left the foreground. Whatever was showing stops: nobody is looking.</summary>
    public void PageHidden()
    {
        bool changed;
        lock (_gate)
        {
            _hidden = true;
            changed = Enter(Phase.Idle, null);
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>The page is in the foreground again after <see cref="PageHidden"/>.</summary>
    public void PageVisible()
    {
        bool changed;
        lock (_gate)
        {
            if (!_hidden) return;
            _hidden = false;
            changed = _inFlight > 0
                ? Enter(Phase.Syncing, SafetyTimeout)
                : Enter(Phase.Settling, SettleWindow);
        }
        if (changed) Changed?.Invoke();
    }

    private void EndCatchUp()
    {
        var changed = false;
        lock (_gate)
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            if (_inFlight == 0 && _phase == Phase.Syncing)
                changed = Enter(Phase.Idle, null);
        }
        if (changed) Changed?.Invoke();
    }

    // Called under the lock. Returns whether IsSyncing changed.
    private bool Enter(Phase phase, TimeSpan? until)
    {
        _timer?.Dispose();
        _timer = null;
        var wasSyncing = _phase == Phase.Syncing;
        _phase = phase;
        var generation = ++_generation;
        if (until is { } due)
            _timer = timeProvider.CreateTimer(OnTimerElapsed, generation, due, Timeout.InfiniteTimeSpan);
        return wasSyncing != (phase == Phase.Syncing);
    }

    private void OnTimerElapsed(object? state)
    {
        bool changed;
        lock (_gate)
        {
            // A timer from a phase that has already been left must not end the current one.
            if (state is not int generation || generation != _generation) return;
            changed = Enter(Phase.Idle, null);
        }
        if (changed) Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private sealed class CatchUp(RealtimeSyncStatus owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.EndCatchUp();
        }
    }
}
