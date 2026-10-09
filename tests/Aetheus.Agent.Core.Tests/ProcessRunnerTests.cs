// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Runtime.InteropServices;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

[Collection(ProcessSpawningTestCollection.Name)]
public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_SuccessfulStderr_PreservesChannelWithoutClaimingFailure()
    {
        var start = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("cmd.exe", "/c echo benign 1>&2")
            : new ProcessStartInfo("/bin/sh", "-c \"echo benign >&2\"");
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        var output = new List<(string Message, TaskLogLevel Level)>();

        var result = await ProcessRunner.RunAsync(
            start,
            timeoutSeconds: 10,
            (message, level) =>
            {
                output.Add((message, level));
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(output, item => item.Message == "[stderr] benign" && item.Level == TaskLogLevel.Warning);
        Assert.Contains(output, item => item.Message == "[process] exit-code=0" && item.Level == TaskLogLevel.Info);
        Assert.DoesNotContain(output, item => item.Level == TaskLogLevel.Error);
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_RecordsSemanticErrorFromExitCode()
    {
        var start = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("cmd.exe", "/c echo diagnostic 1>&2 & exit /b 7")
            : new ProcessStartInfo("/bin/sh", "-c \"echo diagnostic >&2; exit 7\"");
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        var output = new List<(string Message, TaskLogLevel Level)>();

        var result = await ProcessRunner.RunAsync(
            start,
            timeoutSeconds: 10,
            (message, level) =>
            {
                output.Add((message, level));
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains(output, item => item.Message == "[stderr] diagnostic" && item.Level == TaskLogLevel.Warning);
        Assert.Contains(output, item => item.Message == "[process] exit-code=7" && item.Level == TaskLogLevel.Error);
    }

    // A long-running child that prints its own PID first (exec keeps the PID on POSIX).
    private static ProcessStartInfo PidThenSleep()
    {
        var start = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"Write-Output $PID; Start-Sleep -Seconds 60\"")
            : new ProcessStartInfo("/bin/sh", "-c \"echo $$; exec sleep 60\"");
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        return start;
    }

    private static bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [Fact]
    public async Task RunAsync_TaskCancellation_KillsTheChildAndStillReportsCancellation()
    {
        using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = ProcessRunner.RunAsync(
            PidThenSleep(),
            timeoutSeconds: 120,
            (message, _) =>
            {
                if (int.TryParse(message.Trim(), out var value)) pid.TrySetResult(value);
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            taskCts.Token);
        var childPid = await pid.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(HasExited(childPid));

        await taskCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.True(HasExited(childPid));
    }

    [Fact]
    public async Task RunAsync_ChildThatNeverReadsStdin_TimesOutAndIsKilled()
    {
        var pid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Far more than any pipe buffer: the write blocks for as long as the child does not read.
        var result = await ProcessRunner.RunAsync(
            PidThenSleep(),
            timeoutSeconds: 8,
            (message, _) =>
            {
                if (int.TryParse(message.Trim(), out var value)) pid.TrySetResult(value);
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            TestContext.Current.CancellationToken,
            standardInput: new string('x', 16 * 1024 * 1024)).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.True(pid.Task.IsCompletedSuccessfully);
        Assert.True(HasExited(await pid.Task));
    }
}
