// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Tests;

// Exercises realtime reload coalescing directly with an injected delay because bUnit does not drive
// SignalR broadcasts.
public class TrailingReloadCoalescerTests
{
    [Fact]
    public async Task SingleRequest_RunsReloadOnce_AfterTheDelay()
    {
        var gate = new TaskCompletionSource();
        var sut = new TrailingReloadCoalescer(5000, (_, _) => gate.Task);
        var count = 0;

        var pending = sut.RequestAsync(() => { count++; return Task.CompletedTask; }, Xunit.TestContext.Current.CancellationToken);

        Assert.True(sut.IsScheduled);
        Assert.Equal(0, count); // window not elapsed yet - reload hasn't fired

        gate.SetResult();
        await pending;

        Assert.Equal(1, count);
        Assert.False(sut.IsScheduled);
    }

    [Fact]
    public async Task BurstOfRequests_CoalescesToASingleReload()
    {
        var gate = new TaskCompletionSource();
        var sut = new TrailingReloadCoalescer(5000, (_, _) => gate.Task);
        var count = 0;
        Task Reload() { count++; return Task.CompletedTask; }

        var first = sut.RequestAsync(Reload, Xunit.TestContext.Current.CancellationToken);
        var absorbed1 = sut.RequestAsync(Reload, Xunit.TestContext.Current.CancellationToken); // inside the window → absorbed
        var absorbed2 = sut.RequestAsync(Reload, Xunit.TestContext.Current.CancellationToken); // inside the window → absorbed

        Assert.True(absorbed1.IsCompletedSuccessfully); // absorbed requests return immediately
        Assert.True(absorbed2.IsCompletedSuccessfully);
        Assert.Equal(0, count);

        gate.SetResult();
        await Task.WhenAll(first, absorbed1, absorbed2);

        Assert.Equal(1, count); // the whole burst collapsed to one reload
        Assert.False(sut.IsScheduled);
    }

    [Fact]
    public async Task AfterAReloadCompletes_TheNextRequestSchedulesAgain()
    {
        var sut = new TrailingReloadCoalescer(0, (_, _) => Task.CompletedTask); // no real delay
        var count = 0;
        Task Reload() { count++; return Task.CompletedTask; }

        await sut.RequestAsync(Reload, Xunit.TestContext.Current.CancellationToken);
        await sut.RequestAsync(Reload, Xunit.TestContext.Current.CancellationToken); // sequential (window already elapsed) → not absorbed

        Assert.Equal(2, count);
        Assert.False(sut.IsScheduled);
    }

    [Fact]
    public async Task EventDuringReload_TriggersOneAdditionalReloadWithoutBeingLost()
    {
        var reloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirstReload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = new TrailingReloadCoalescer(0, (_, _) => Task.CompletedTask);
        var count = 0;
        async Task Reload()
        {
            count++;
            if (count == 1)
            {
                reloadStarted.SetResult();
                await finishFirstReload.Task;
            }
        }

        var first = sut.RequestAsync(Reload, Xunit.TestContext.Current.CancellationToken);
        await reloadStarted.Task;
        await sut.RequestAsync(Reload, Xunit.TestContext.Current.CancellationToken);
        await sut.RequestAsync(Reload, Xunit.TestContext.Current.CancellationToken);
        finishFirstReload.SetResult();
        await first;

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CancelledLifetime_PreventsDeferredReload()
    {
        using var cts = new CancellationTokenSource();
        var reloadCount = 0;
        var sut = new TrailingReloadCoalescer(5000);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.RequestAsync(() => { reloadCount++; return Task.CompletedTask; }, cts.Token));

        Assert.Equal(0, reloadCount);
        Assert.False(sut.IsScheduled);
    }
}
