// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The scanner container monitor samples <c>docker stats</c> every five seconds while the scanner runs and
/// keeps the peak of each figure. The readings are handed in, so no process is started and no time passes.
/// </summary>
public sealed class ScannerContainerMonitorTests
{
    [Fact]
    public async Task Samples_KeepThePeakOfEachFigure_AndSkipAFailedOrUnreadableReading()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new ImmediateTimers();
        var readings = new Queue<string?>(
        [
            "10%|1MiB / 2GiB|1MB / 2MB|3kB / 4kB",
            null,
            "not docker stats",
            "5%|3MiB / 2GiB|0B / 5MB|1kB / 9kB"
        ]);
        var names = new List<string>();

        var usage = await ScannerProcessRunner.MonitorContainerAsync(
            "scan-1",
            (name, _) =>
            {
                names.Add(name);
                var reading = readings.Dequeue();
                if (readings.Count == 0) stop.Cancel();
                return Task.FromResult(reading);
            },
            clock,
            stop.Token);

        Assert.Equal(["scan-1", "scan-1", "scan-1", "scan-1"], names);
        Assert.Equal(new ScannerProcessRunner.ContainerUsage(10, 3_145_728, 1_000_000, 5_000_000, 3_000, 9_000), usage);
        Assert.All(clock.DueTimes, due => Assert.Equal(TimeSpan.FromSeconds(5), due));
        Assert.Equal(3, clock.DueTimes.Count);
    }

    [Fact]
    public async Task AMonitorStoppedBeforeItsFirstSample_ReportsNoUsage()
    {
        var reads = 0;

        var usage = await ScannerProcessRunner.MonitorContainerAsync(
            "scan-1",
            (_, _) => { reads++; return Task.FromResult<string?>(null); },
            new ImmediateTimers(),
            new CancellationToken(canceled: true));

        Assert.Equal(0, reads);
        Assert.Same(ScannerProcessRunner.ContainerUsage.Empty, usage);
    }

    [Fact]
    public async Task AReadingCancelledWithTheScanner_EndsTheMonitorWithWhatItHad()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = true;

        var usage = await ScannerProcessRunner.MonitorContainerAsync(
            "scan-1",
            (_, ct) =>
            {
                if (first)
                {
                    first = false;
                    return Task.FromResult<string?>("7%|2MiB / 2GiB|0B / 0B|0B / 0B");
                }
                stop.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<string?>(null);
            },
            new ImmediateTimers(),
            stop.Token);

        Assert.Equal(7, usage.PeakCpuPercent);
        Assert.Equal(2_097_152, usage.PeakMemoryBytes);
    }

    [Fact]
    public async Task ADockerThatCannotRun_FailsTheMonitor()
    {
        await Assert.ThrowsAsync<Win32Exception>(() => ScannerProcessRunner.MonitorContainerAsync(
            "scan-1",
            (_, _) => throw new Win32Exception(2, "docker not found"),
            new ImmediateTimers(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACancellationThatIsNotTheScanners_IsNotSwallowed()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => ScannerProcessRunner.MonitorContainerAsync(
            "scan-1",
            (_, _) => throw new OperationCanceledException(),
            new ImmediateTimers(),
            TestContext.Current.CancellationToken));
    }

    /// <summary>The system clock, except that every timer fires at once; it records the delays asked for.</summary>
    private sealed class ImmediateTimers : TimeProvider
    {
        private readonly List<TimeSpan> _dueTimes = [];

        public IReadOnlyList<TimeSpan> DueTimes
        {
            get { lock (_dueTimes) return [.. _dueTimes]; }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_dueTimes) _dueTimes.Add(dueTime);
            return base.CreateTimer(callback, state, TimeSpan.Zero, period);
        }
    }
}
