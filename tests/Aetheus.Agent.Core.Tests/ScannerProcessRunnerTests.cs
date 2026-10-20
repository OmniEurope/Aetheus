// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

[Collection(ProcessSpawningTestCollection.Name)]
public sealed class ScannerProcessRunnerTests
{
    [Theory]
    [InlineData("12.5%|64MiB / 2GiB", 12.5, 67_108_864)]
    [InlineData("0.01%|1.5GB / 2GB", 0.01, 1_500_000_000)]
    [InlineData("100%|512KiB / 1GiB", 100, 524_288)]
    public void TryParseUsage_ParsesDockerStats(string value, double expectedCpu, long expectedMemory)
    {
        var parsed = ScannerProcessRunner.TryParseUsage(value, out var cpu, out var memory);

        Assert.True(parsed);
        Assert.Equal(expectedCpu, cpu);
        Assert.Equal(expectedMemory, memory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("n/a|64MiB / 2GiB")]
    [InlineData("12%|unknown")]
    public void TryParseUsage_RejectsInvalidDockerStats(string value)
    {
        Assert.False(ScannerProcessRunner.TryParseUsage(value, out _, out _));
    }

    [Theory]
    [InlineData("7B", 7)]
    [InlineData("2kb", 2_000)]
    [InlineData("3MB", 3_000_000)]
    [InlineData("1,5MiB", 1_572_864)]
    [InlineData("2GB", 2_000_000_000)]
    [InlineData("1 GiB", 1_073_741_824)]
    public void TryParseUsage_ConvertsEveryDockerSizeUnit(string memory, long expectedBytes)
    {
        Assert.True(ScannerProcessRunner.TryParseUsage($"1%|{memory} / 4GiB", out _, out var bytes));
        Assert.Equal(expectedBytes, bytes);
    }

    [Theory]
    [InlineData("5TB")]
    [InlineData("MiB")]
    [InlineData("1.2.3MB")]
    [InlineData("12")]
    public void TryParseUsage_RejectsUnknownUnitsAndMalformedAmounts(string memory)
    {
        Assert.False(ScannerProcessRunner.TryParseUsage($"1%|{memory} / 4GiB", out _, out var bytes));
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void TryParseUsage_RejectsIncompleteIoColumns()
    {
        Assert.False(ScannerProcessRunner.TryParseUsage(
            "1%|1MiB / 2GiB|1MB|2MB / 3MB", out _, out _, out _, out _, out _, out _));
        Assert.False(ScannerProcessRunner.TryParseUsage(
            "1%|1MiB / 2GiB|1MB / 2MB", out _, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void TryParseUsage_ParsesCpuMemoryDiskAndNetwork()
    {
        var parsed = ScannerProcessRunner.TryParseUsage(
            "12.5%|64MiB / 2GiB|1.5MB / 2MiB|3kB / 4KiB",
            out var cpu,
            out var memory,
            out var diskRead,
            out var diskWritten,
            out var networkReceived,
            out var networkSent);

        Assert.True(parsed);
        Assert.Equal(12.5, cpu);
        Assert.Equal(67_108_864, memory);
        Assert.Equal(1_500_000, diskRead);
        Assert.Equal(2_097_152, diskWritten);
        Assert.Equal(3_000, networkReceived);
        Assert.Equal(4_096, networkSent);
    }

    [Fact]
    public async Task RunAsync_TimeoutKillsProcessTreeWithinBound()
    {
        var runner = new ScannerProcessRunner(
            Options.Create(new AetheusAgentOptions { MinTimeoutSeconds = 1, MaxTimeoutSeconds = 60 }),
            NullLogger<ScannerProcessRunner>.Instance);
        var process = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            process.ArgumentList.Add("-NoProfile");
            process.ArgumentList.Add("-Command");
            process.ArgumentList.Add("Start-Sleep -Seconds 30");
        }
        else
        {
            process.ArgumentList.Add("-c");
            process.ArgumentList.Add("sleep 30");
        }
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunAsync(process, 1, (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Timeout kill took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_ExternalCancellationKillsProcessTreeAndPropagatesCancellation()
    {
        var runner = new ScannerProcessRunner(
            Options.Create(new AetheusAgentOptions { MinTimeoutSeconds = 1, MaxTimeoutSeconds = 60 }),
            NullLogger<ScannerProcessRunner>.Instance);
        var process = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            process.ArgumentList.Add("-NoProfile");
            process.ArgumentList.Add("-Command");
            process.ArgumentList.Add("$PID; Start-Sleep -Seconds 30");
        }
        else
        {
            process.ArgumentList.Add("-c");
            process.ArgumentList.Add("echo $$; sleep 30");
        }
        var processId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var running = runner.RunAsync(process, 60, (line, _) =>
        {
            if (int.TryParse(line, out var id)) processId.TrySetResult(id);
            return Task.CompletedTask;
        }, cancellation.Token);
        // Waiting for the child to announce itself is a precondition, not the behaviour under test.
        // Windows PowerShell 5.1 was measured starting in 3.6s to 7.1s on this machine even when idle,
        // so a 5s budget here made the run depend on how busy the box was rather than on whether
        // cancellation kills the process tree. The assertions that bound how long the kill may take
        // are deliberately left alone.
        var id = await processId.Task.WaitAsync(
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(SpinWait.SpinUntil(() => ProcessHasExited(id), TimeSpan.FromSeconds(5)),
            $"Scanner process {id} survived cancellation.");
    }

    [Fact]
    public async Task CompletesWithinAsync_NonCooperativeMonitor_IsBounded()
    {
        var monitor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        var completed = await ScannerProcessRunner.CompletesWithinAsync(
            monitor.Task,
            TimeSpan.FromMilliseconds(50));

        Assert.False(completed);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Monitor wait took {stopwatch.Elapsed}.");
    }

    private static bool ProcessHasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
