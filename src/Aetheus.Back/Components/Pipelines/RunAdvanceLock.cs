// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Refcounted keyed async lock serializing per-run stage advancement.
/// The previous ConcurrentDictionary + TryRemove-in-finally pattern could evict a
/// semaphore while another caller was still waiting on it, while a third caller got a
/// fresh one - two AdvanceStage passes then ran concurrently for the same run (double
/// task dispatch). Here an entry is only evicted when its last holder/waiter releases
/// it, and the semaphore is disposed at that point (they never were before).
/// </summary>
internal sealed class RunAdvanceLock
{
    /// <summary>
    /// The one process-wide instance. Stage advancement and run cancellation must serialize against
    /// each other, and they now live in two different classes - two Scoped instances each holding their
    /// own dictionary would let a cancellation and an advancement run concurrently for the same run,
    /// which is exactly the double-dispatch this lock exists to prevent. Static because the guarantee
    /// is per-process, not per-request.
    /// </summary>
    public static readonly RunAdvanceLock Shared = new();

    private readonly Dictionary<int, Entry> _entries = new();
    private readonly object _gate = new();
    private readonly Action<int, Entry> _releaseReference;

    public RunAdvanceLock() => _releaseReference = ReleaseRef;

    internal sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }

    public async Task<Releaser> AcquireAsync(int runId, CancellationToken ct)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(runId, out entry!))
            {
                entry = new Entry();
                _entries[runId] = entry;
            }
            entry.RefCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ReleaseRef(runId, entry);
            throw;
        }
        return new Releaser(_releaseReference, runId, entry);
    }

    private void ReleaseRef(int runId, Entry entry)
    {
        lock (_gate)
        {
            entry.RefCount--;
            if (entry.RefCount == 0 && _entries.TryGetValue(runId, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(runId);
                entry.Semaphore.Dispose();
            }
        }
    }

    /// <summary>Releases the run's slot. It reaches the lock only through the release delegate it is given, so
    /// the lock and its releaser do not depend on each other.</summary>
    internal readonly struct Releaser(Action<int, Entry> releaseReference, int runId, Entry entry) : IDisposable
    {
        public void Dispose()
        {
            entry.Semaphore.Release();
            releaseReference(runId, entry);
        }
    }
}
