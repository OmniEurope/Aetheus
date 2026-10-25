// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Middleware;
using Aetheus.Back.Services;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests;

/// <summary>R1-40: the runtime figures around a request, and the background work tracker beside them.</summary>
public sealed class RuntimePauseSnapshotTests
{
    [Fact]
    public void TheDifference_SubtractsEventsAndKeepsTheThreadPoolLevels()
    {
        var earlier = new RuntimePauseSnapshot(TimeSpan.FromMilliseconds(40), 10, 5, 2, 7, 12, 100);
        var later = new RuntimePauseSnapshot(TimeSpan.FromMilliseconds(2090), 14, 6, 3, 30, 19, 160);

        var delta = later.Since(earlier);

        Assert.Equal(TimeSpan.FromMilliseconds(2050), delta.GcPause);
        Assert.Equal((4, 1, 1), (delta.Gen0Collections, delta.Gen1Collections, delta.Gen2Collections));
        Assert.Equal(60, delta.LockContentions);
        // A backlog and a thread count are levels: the later reading is the one that matters.
        Assert.Equal(30, delta.ThreadPoolPendingWorkItems);
        Assert.Equal(19, delta.ThreadPoolThreads);
    }

    [Fact]
    public void ABlockingCollection_ShowsInTheCapturedCounts_AndAddsToTheTotalPause()
    {
        var before = RuntimePauseSnapshot.Capture();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        var delta = RuntimePauseSnapshot.Capture().Since(before);

        Assert.True(delta.Gen2Collections >= 1);
        Assert.True(delta.GcPause >= TimeSpan.Zero);
    }

    [Fact]
    public void TheTracker_NamesWhatRunsOrEndedAtOrAfterTheInstant_AndNothingOlder()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 13, 0, 0, TimeSpan.Zero));
        var tracker = new BackgroundWorkTracker(clock);
        tracker.Begin("Old").Dispose();
        clock.Advance(TimeSpan.FromMinutes(5));
        var since = clock.GetUtcNow();
        using var running = tracker.Begin("Running");
        tracker.Begin("Recent").Dispose();

        Assert.Equal(["Recent (ended)", "Running (running)"], tracker.Overlapping(since));
    }

    [Fact]
    public void TheTracker_CountsOverlappingIterationsOfOneName_AndDisposingTwiceEndsOnce()
    {
        var clock = new FakeTimeProvider();
        var tracker = new BackgroundWorkTracker(clock);
        var first = tracker.Begin("Loop");
        var second = tracker.Begin("Loop");

        first.Dispose();
        first.Dispose();
        Assert.Equal(["Loop (running)"], tracker.Overlapping(clock.GetUtcNow().AddHours(1)));

        second.Dispose();
        Assert.Empty(tracker.Overlapping(clock.GetUtcNow().AddHours(1)));
    }

    [Fact]
    public void TheTracker_RefusesABlankName()
    {
        Assert.Throws<ArgumentException>(() => new BackgroundWorkTracker(TimeProvider.System).Begin(" "));
    }
}
