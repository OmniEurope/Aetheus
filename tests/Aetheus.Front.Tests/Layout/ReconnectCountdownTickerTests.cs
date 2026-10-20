// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Aetheus.Front.Tests.TestDoubles;

namespace Aetheus.Front.Tests.Layout;

/// <summary>
/// The countdown runs on a simulated clock: each second is moved once the countdown has armed its wait
/// for it (moved earlier, the clock would pass before the wait exists), and a tick is read once the
/// countdown said it.
/// </summary>
public class ReconnectCountdownTickerTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private readonly ObservedFakeClock _time = new(DateTimeOffset.UnixEpoch);
    private readonly StateSignal _ticked = new();

    private Func<Task> Record(ReconnectCountdownTicker ticker, List<int> ticks) => () =>
    {
        lock (ticks) ticks.Add(ticker.Remaining);
        _ticked.Pulse();
        return Task.CompletedTask;
    };

    private static int Count(List<int> ticks)
    {
        lock (ticks) return ticks.Count;
    }

    /// <summary>Moves the clock past the <paramref name="armed"/>-th one-second wait once it exists, then
    /// waits for the tick it causes.</summary>
    private async Task NextSecondAsync(int armed, List<int> ticks)
    {
        await _time.WhenArmedAsync(Second, armed);
        var before = Count(ticks);
        _time.Advance(Second);
        await _ticked.WhenAsync(() => Count(ticks) > before, $"the tick after wait {armed}");
    }

    [Fact]
    public async Task RunAsync_CountsDownSecondBySecond()
    {
        var ticker = new ReconnectCountdownTicker(_time);
        var ticks = new List<int>();

        var run = ticker.RunAsync(3, Record(ticker, ticks));

        Assert.Equal(3, ticks.Last());

        await NextSecondAsync(1, ticks);
        Assert.Equal(2, ticks.Last());

        await NextSecondAsync(2, ticks);
        Assert.Equal(1, ticks.Last());

        await NextSecondAsync(3, ticks);
        await HangGuard.AwaitAsync(run, "the countdown ending");

        Assert.Equal(0, ticks.Last());
        Assert.Equal([3, 2, 1, 0], ticks);
    }

    [Fact]
    public async Task Stop_CancelsRunInFlight()
    {
        var ticker = new ReconnectCountdownTicker(_time);
        var ticks = new List<int>();

        var run = ticker.RunAsync(5, Record(ticker, ticks));

        await NextSecondAsync(1, ticks);

        ticker.Stop();
        await HangGuard.AwaitAsync(run, "the stopped countdown ending");

        Assert.Equal(0, ticker.Remaining);
        Assert.DoesNotContain(0, ticks.Skip(1));
    }

    [Fact]
    public async Task SecondRun_SupersedesTheFirst()
    {
        var ticker = new ReconnectCountdownTicker(_time);
        var firstTicks = new List<int>();
        var secondTicks = new List<int>();

        var first = ticker.RunAsync(10, Record(ticker, firstTicks));

        await NextSecondAsync(1, firstTicks);
        await _time.WhenArmedAsync(Second, 2);

        var second = ticker.RunAsync(2, Record(ticker, secondTicks));
        await HangGuard.AwaitAsync(first, "the superseded countdown ending");

        // Waits 1 and 2 were the first run's; the second run armed the third.
        await NextSecondAsync(3, secondTicks);
        await NextSecondAsync(4, secondTicks);
        await HangGuard.AwaitAsync(second, "the second countdown ending");

        Assert.Equal([2, 1, 0], secondTicks);
    }

    [Fact]
    public void Dispose_ClearsState()
    {
        var ticker = new ReconnectCountdownTicker(_time);
        _ = ticker.RunAsync(5, () => Task.CompletedTask);

        ticker.Dispose();

        Assert.Equal(0, ticker.Remaining);
    }
}
