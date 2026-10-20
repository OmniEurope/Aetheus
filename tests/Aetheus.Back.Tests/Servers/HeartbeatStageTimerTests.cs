// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Servers;

/// <summary>A slow heartbeat says how long each of its stages took; a normal one writes
/// nothing.</summary>
public sealed class HeartbeatStageTimerTests
{
    [Fact]
    public void EachStage_IsTimedFromThePreviousMark()
    {
        var clock = new FakeTimeProvider();
        var timer = new HeartbeatStageTimer(clock);

        clock.Advance(TimeSpan.FromMilliseconds(40));
        timer.Mark("presence");
        clock.Advance(TimeSpan.FromMilliseconds(6000));
        timer.Mark("inventories");

        Assert.Equal(
            new[] { ("presence", TimeSpan.FromMilliseconds(40)), ("inventories", TimeSpan.FromMilliseconds(6000)) },
            timer.Stages);
        Assert.Equal(TimeSpan.FromMilliseconds(6040), timer.Total);
    }

    /// <summary>2 to 10 s is Information (common while a host compiles or deploys), 10 s and more a
    /// Warning.</summary>
    [Fact]
    public void OnlyASlowHeartbeat_IsWritten_AsInformationBelowTheWarningThreshold()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();

        var fast = new HeartbeatStageTimer(clock);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        fast.Mark("presence");
        fast.LogIfSlow(logger, 7);
        Assert.Empty(logger.Entries);

        var slow = new HeartbeatStageTimer(clock);
        clock.Advance(TimeSpan.FromMilliseconds(40));
        slow.Mark("presence");
        clock.Advance(HeartbeatStageTimer.SlowThreshold);
        slow.Mark("inventories");
        slow.LogIfSlow(logger, 7);

        var (level, message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, level);
        Assert.Equal("Heartbeat of server 7 took 2040 ms: presence 40 ms, inventories 2000 ms", message);
    }

    [Theory]
    [InlineData(9_999, LogLevel.Information)]
    [InlineData(10_000, LogLevel.Warning)]
    [InlineData(12_500, LogLevel.Warning)]
    public void TheWarningLevel_StartsAtTenSeconds(int totalMs, LogLevel expected)
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var timer = new HeartbeatStageTimer(clock);
        clock.Advance(TimeSpan.FromMilliseconds(totalMs));
        timer.Mark("inventories");

        timer.LogIfSlow(logger, 7);

        Assert.Equal(TimeSpan.FromSeconds(10), HeartbeatStageTimer.WarningThreshold);
        Assert.Equal(expected, Assert.Single(logger.Entries).Level);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
