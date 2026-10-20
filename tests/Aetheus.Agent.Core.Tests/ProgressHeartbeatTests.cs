// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The progress lines of a long command: written while it runs, never after it ends, and never a
/// change to its outcome.
/// </summary>
public sealed class ProgressHeartbeatTests
{
    private readonly List<TimeSpan> _beats = [];

    private Task Report(TimeSpan elapsed)
    {
        lock (_beats) _beats.Add(elapsed);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ALongCommandIsReportedWhileItRunsAndTheResultIsReturnedUnchanged()
    {
        // The command stays alive until the first heartbeat is observed (guarded at 10 s), so the
        // test depends on a signal and never on how fast the machine schedules a 400 ms sleep.
        var firstBeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await ProgressHeartbeat.RunAsync(
            async () =>
            {
                await firstBeat.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                return 42;
            },
            TimeSpan.FromMilliseconds(50),
            async elapsed =>
            {
                await Report(elapsed);
                firstBeat.TrySetResult();
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
        Assert.NotEmpty(_beats);
        Assert.All(_beats, elapsed => Assert.True(elapsed > TimeSpan.Zero));
    }

    [Fact]
    public async Task NoLineIsWrittenOnceTheCommandHasEnded()
    {
        await ProgressHeartbeat.RunAsync(
            () => Task.FromResult(0), TimeSpan.FromMilliseconds(20), Report, TestContext.Current.CancellationToken);
        int afterEnd;
        lock (_beats) afterEnd = _beats.Count;

        await Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken);

        lock (_beats) Assert.Equal(afterEnd, _beats.Count);
    }

    [Fact]
    public async Task AFailureOfTheCommandPropagatesAndStopsTheLines()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ProgressHeartbeat.RunAsync<int>(
            async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
                throw new InvalidOperationException("runner crashed");
            },
            TimeSpan.FromMilliseconds(20),
            Report,
            TestContext.Current.CancellationToken));
        int afterEnd;
        lock (_beats) afterEnd = _beats.Count;
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        Assert.Equal("runner crashed", failure.Message);
        lock (_beats) Assert.Equal(afterEnd, _beats.Count);
    }

    [Fact]
    public async Task ANonPositiveIntervalIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ProgressHeartbeat.RunAsync(
            () => Task.FromResult(0), TimeSpan.Zero, Report, TestContext.Current.CancellationToken));
    }
}
