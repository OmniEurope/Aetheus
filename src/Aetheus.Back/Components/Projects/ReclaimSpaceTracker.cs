// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>
/// PLAN-013 lot 6: which table this backend is giving space back from, if any, and the last results. A
/// singleton, so one « Récupérer la place » runs at a time and the page can follow it after its request
/// returned; the vacuum itself outlives the request (a large table takes minutes).
/// </summary>
public sealed class ReclaimSpaceTracker
{
    internal const int HistorySize = 20;

    private readonly object _sync = new();
    private readonly LinkedList<ReclaimSpaceResultDto> _history = new();
    private ReclaimSpaceResultDto? _running;

    /// <summary>The vacuum in progress, for a caller (a test) that needs to wait for it.</summary>
    internal Task Current { get; private set; } = Task.CompletedTask;

    public string? RunningTable
    {
        get { lock (_sync) return _running?.Table; }
    }

    public List<ReclaimSpaceResultDto> History
    {
        get
        {
            lock (_sync)
                return _running is null ? [.. _history] : [_running, .. _history];
        }
    }

    /// <summary>Takes the slot; false while another table is being processed.</summary>
    internal bool TryBegin(ReclaimSpaceResultDto started)
    {
        lock (_sync)
        {
            if (_running is not null) return false;
            _running = started;
            return true;
        }
    }

    internal void Track(Task work) => Current = work;

    internal void Complete(ReclaimSpaceResultDto result)
    {
        lock (_sync)
        {
            _running = null;
            _history.AddFirst(result);
            while (_history.Count > HistorySize) _history.RemoveLast();
        }
    }

    /// <summary>Gives the slot back without a result, when the start itself was refused.</summary>
    internal void Abandon()
    {
        lock (_sync) _running = null;
    }
}
