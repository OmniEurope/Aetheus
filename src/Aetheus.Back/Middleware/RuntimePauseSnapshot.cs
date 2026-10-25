// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Middleware;

/// <summary>
/// What the runtime says about itself at one instant, read without allocating: the garbage collector's
/// total pause, its collection counts, the thread pool's backlog and the monitor lock contention. The
/// difference of two snapshots taken around a request tells whether the process itself paused during it
/// (recette R1-40: several routes slowed by exactly 2 s at the same moment). All values are process-wide.
/// </summary>
internal readonly record struct RuntimePauseSnapshot(
    TimeSpan GcPause,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long ThreadPoolPendingWorkItems,
    int ThreadPoolThreads,
    long LockContentions)
{
    public static RuntimePauseSnapshot Capture() => new(
        GC.GetTotalPauseDuration(),
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        ThreadPool.PendingWorkItemCount,
        ThreadPool.ThreadCount,
        Monitor.LockContentionCount);

    /// <summary>
    /// What happened between <paramref name="earlier"/> and this snapshot: pauses and collections done in
    /// between. The thread pool figures are the state now, not a difference (a backlog is a level).
    /// </summary>
    public RuntimePauseSnapshot Since(RuntimePauseSnapshot earlier) => this with
    {
        GcPause = GcPause - earlier.GcPause,
        Gen0Collections = Gen0Collections - earlier.Gen0Collections,
        Gen1Collections = Gen1Collections - earlier.Gen1Collections,
        Gen2Collections = Gen2Collections - earlier.Gen2Collections,
        LockContentions = LockContentions - earlier.LockContentions
    };
}
