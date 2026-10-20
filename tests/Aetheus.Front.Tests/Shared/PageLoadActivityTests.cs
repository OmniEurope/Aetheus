// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// When the page-load bar under the top bar shows and hides. The windows run on a clock of the test's own:
/// on the system clock a loaded machine let the grace period elapse between two lines of a test (B7bis),
/// and the window closed before the request it was opened for was counted.
/// </summary>
public sealed class PageLoadActivityTests
{

    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void RequestOutsideANavigation_IsNotCounted()
    {
        var activity = new PageLoadActivity(_time);

        Assert.Null(activity.TrackStart());
        Assert.False(activity.IsActive);
    }

    [Fact]
    public async Task Navigation_StaysActiveWhileItsRequestRuns_ThenCloses()
    {
        var activity = new PageLoadActivity(_time);
        activity.Begin();
        var window = activity.TrackStart();
        Assert.NotNull(window);

        // Well past the grace period: the pending request keeps the bar on.
        _time.Advance(PageLoadActivity.Grace + TimeSpan.FromMilliseconds(300));
        Assert.True(activity.IsActive);

        activity.TrackEnd(window.Value);
        _time.Advance(PageLoadActivity.Settle);
        await ClosedAsync(activity);
        Assert.False(activity.IsActive);
    }

    [Fact]
    public async Task NavigationWithoutRequests_ClosesAfterTheGracePeriod()
    {
        var activity = new PageLoadActivity(_time);
        var changes = 0;
        activity.Changed += () => Interlocked.Increment(ref changes);

        activity.Begin();
        Assert.True(activity.IsActive);

        _time.Advance(PageLoadActivity.Grace);
        await ClosedAsync(activity);
        Assert.False(activity.IsActive);
        Assert.Equal(2, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task ARequestOfAnOlderNavigation_DoesNotCloseTheNewOne()
    {
        var activity = new PageLoadActivity(_time);
        activity.Begin();
        var oldWindow = activity.TrackStart()!.Value;
        activity.Begin();
        var newWindow = activity.TrackStart()!.Value;

        activity.TrackEnd(oldWindow);
        _time.Advance(PageLoadActivity.Grace + TimeSpan.FromMilliseconds(300));
        Assert.True(activity.IsActive);

        activity.TrackEnd(newWindow);
        _time.Advance(PageLoadActivity.Settle);
        await ClosedAsync(activity);
        Assert.False(activity.IsActive);
    }

    // The window closes in the continuation of the elapsed delay, which may run on another thread: the
    // wait is on the activity's own Changed signal, re-checked each time it says so.
    private static async Task ClosedAsync(PageLoadActivity activity)
    {
        var changed = new StateSignal();
        activity.Changed += changed.Pulse;
        try
        {
            await changed.WhenAsync(() => !activity.IsActive, "the load window closed");
        }
        finally
        {
            activity.Changed -= changed.Pulse;
        }
    }
}
