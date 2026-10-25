// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>
/// Per-app metric-ingest serialization (singleton). The metric-name cardinality cap reads a snapshot of the
/// app's name set, computes a budget (<c>200 - count</c>) and writes new names back; two concurrent ingests
/// for the same app would both see the same budget and race past the cap (last-writer-wins on the cache). A
/// per-app <see cref="SemaphoreSlim"/> serializes ingestion for a single app so the budget stays accurate,
/// while different apps never contend.
/// </summary>
public sealed class AppIngestGate
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public SemaphoreSlim For(int appId) => _locks.GetOrAdd(appId, static _ => new SemaphoreSlim(1, 1));

    private readonly ConcurrentDictionary<int, SemaphoreSlim> _analyticsLocks = new();

    /// <summary>Use an independent audience-ingestion lock because it protects route and storage
    /// budgets, while metrics ingestion protects metric-name limits.</summary>
    public SemaphoreSlim ForAnalytics(int appId) =>
        _analyticsLocks.GetOrAdd(appId, static _ => new SemaphoreSlim(1, 1));
}
