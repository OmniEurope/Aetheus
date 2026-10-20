// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.Services;
using Aetheus.Front.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Run 2458: after the blue-green Switch of aetheus-deploy-prod the run page's socket stayed on the
/// previous colour and the page stopped following the run. The new-version signal must restart that
/// same connection and reload the run once. The reload's debounce runs on a simulated clock, so "once"
/// is read from the timers the connection armed, not from a pause.
/// </summary>
public class PipelineRunLiveConnectionBackendSwitchTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task WhenTheBackendIsReplaced_TheSameConnectionRestartsAndTheRunReloadsOnce()
    {
        var auth = new AuthStateProvider(new FakeJsRuntime(), NullLogger<AuthStateProvider>.Instance);
        var factory = new SilentHubConnectionFactory(auth);
        var clock = new ObservedFakeClock(DateTimeOffset.UnixEpoch);
        var reloads = 0;
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new PipelineRunLiveConnection(
            factory, NullLogger.Instance, 2458, () => [],
            () => { Interlocked.Increment(ref reloads); reloaded.TrySetResult(); return Task.CompletedTask; },
            () => Task.CompletedTask,
            callback => callback(),
            timeProvider: clock);
        await connection.StartAsync();
        var hub = factory.Connection!;
        Assert.Equal(1, hub.Starts);

        factory.AnnounceBackendReplaced();
        await clock.WhenArmedAsync(Debounce, 1);
        clock.Advance(Debounce);
        await HangGuard.AwaitAsync(reloaded.Task, "the run reloaded after the restart");

        // One debounced reload was scheduled, and none is left that could reload again.
        Assert.Equal(1, clock.Armed(Debounce));
        Assert.Equal(0, clock.Pending(Debounce));
        Assert.Equal(1, factory.Created);
        Assert.Equal(2, hub.Starts);
        Assert.Equal(1, Volatile.Read(ref reloads));

        await connection.DisposeAsync();
        // Disposed, the connection no longer listens: the signal is handled (or not) within the call.
        factory.AnnounceBackendReplaced();
        Assert.Equal(2, hub.Starts);
        Assert.Equal(1, clock.Armed(Debounce));
    }
}
