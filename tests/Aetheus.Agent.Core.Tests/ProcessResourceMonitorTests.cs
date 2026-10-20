// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Agent.Core.Executors;

namespace Aetheus.Agent.Core.Tests;

[Collection(ProcessSpawningTestCollection.Name)]
public sealed class ProcessResourceMonitorTests
{
    [Fact]
    public void Accumulator_AggregatesProcessTreeCountersAndSimultaneousPeaks()
    {
        var accumulator = new ProcessResourceAccumulator();
        accumulator.Add(
        [
            new ProcessResourceSample(10, 100, TimeSpan.FromSeconds(1), 100, 10, 1000, 2000),
            new ProcessResourceSample(11, 110, TimeSpan.FromSeconds(2), 200, 20, 3000, 4000)
        ]);
        accumulator.Add(
        [
            new ProcessResourceSample(10, 100, TimeSpan.FromSeconds(3), 150, 15, 1500, 2500),
            new ProcessResourceSample(11, 110, TimeSpan.FromSeconds(4), 175, 25, 3500, 4500),
            new ProcessResourceSample(12, 120, TimeSpan.FromSeconds(1), 50, 5, 500, 750)
        ]);

        var usage = accumulator.Build();

        Assert.Equal(TimeSpan.FromSeconds(8), usage.CpuTime);
        Assert.Equal(375, usage.PeakWorkingSetBytes);
        Assert.Equal(45, usage.PeakSwapBytes);
        Assert.Equal(5500, usage.DiskReadBytes);
        Assert.Equal(7750, usage.DiskWrittenBytes);
    }

    [Fact]
    public async Task CaptureTree_IncludesARealChildInAggregatedWorkingSet()
    {


        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("pwsh")
            : new ProcessStartInfo("/bin/sh");
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("Start-Sleep -Seconds 10");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("sleep 10");
        }

        using var child = Process.Start(startInfo)!;
        try
        {
            // The child is visible to the tree walk only once the OS has registered it: poll for that
            // (bounded at 10 s) instead of guessing it takes less than 250 ms on a loaded machine.
            using var root = Process.GetCurrentProcess();
            var samples = ProcessResourceMonitor.CaptureTree(root);
            var polling = Stopwatch.StartNew();
            while (!samples.Any(sample => sample.ProcessId == child.Id) && polling.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
                samples = ProcessResourceMonitor.CaptureTree(root);
            }

            var childSample = Assert.Single(samples, sample => sample.ProcessId == child.Id);
            Assert.True(childSample.WorkingSetBytes > 0);

            var accumulator = new ProcessResourceAccumulator();
            accumulator.Add(samples);

            Assert.Equal(samples.Sum(sample => sample.WorkingSetBytes), accumulator.Build().PeakWorkingSetBytes);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PublishAsync_EmitsTypedMetricsWithoutProcessOutput()
    {
        var output = new List<string>();
        await ProcessResourceMonitor.PublishAsync(
            new ProcessResourceUsage(TimeSpan.FromSeconds(1.25), 2048, 0, 4096, 8192),
            (line, level) =>
            {
                Assert.Equal(TaskLogLevel.Info, level);
                output.Add(line);
                return Task.CompletedTask;
            });

        Assert.Contains(output, line => line.Contains("key=step.cpu;type=Duration;unit=s]1.25", StringComparison.Ordinal));
        Assert.Contains(output, line => line.Contains("key=step.memory.peak;type=Size;unit=bytes]2048", StringComparison.Ordinal));
        Assert.DoesNotContain(output, line => line.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }
}
