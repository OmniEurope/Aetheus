// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Short-lived, component-scoped cache for immutable portions of completed pipeline runs.
/// It accelerates live refreshes and navigation inside an orchestration tree without sharing
/// authorization-scoped data between users or hiding analysis updates for more than a few seconds.
/// </summary>
internal sealed class PipelineRunPageCache(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(20);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<int, CacheEntry<PipelineRunDto>> _runs = [];
    private readonly ConcurrentDictionary<int, CacheEntry<AnalysisRunGateDto>> _gates = [];

    public bool TryGetRun(int runId, out PipelineRunDto? run) =>
        TryGet(_runs, runId, out run);

    public void StoreRun(PipelineRunDto run)
    {
        if (PipelineRunFormatting.IsTerminal(run.Status))
            _runs[run.Id] = new(run, _timeProvider.GetUtcNow());
    }

    public bool TryGetGate(int runId, out AnalysisRunGateDto? gate) =>
        TryGet(_gates, runId, out gate);

    public void StoreGate(PipelineRunDto run, AnalysisRunGateDto gate)
    {
        if (PipelineRunFormatting.IsTerminal(run.Status) && gate.MissingProducers.Count == 0)
            _gates[run.Id] = new(gate, _timeProvider.GetUtcNow());
    }

    /// <summary> A finding's decision reverted from the Gate tab changes the status the cached
    /// gates still carry, for the run and the runs of its tree alike; the next read goes to the
    /// server. </summary>
    public void ForgetGates() => _gates.Clear();

    private bool TryGet<T>(ConcurrentDictionary<int, CacheEntry<T>> entries, int id, out T? value)
        where T : class
    {
        if (entries.TryGetValue(id, out var entry)
            && _timeProvider.GetUtcNow() - entry.StoredAt <= Lifetime)
        {
            value = entry.Value;
            return true;
        }

        entries.TryRemove(id, out _);
        value = null;
        return false;
    }

    private sealed record CacheEntry<T>(T Value, DateTimeOffset StoredAt);
}
