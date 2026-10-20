// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.IO.Pipes;
using Aetheus.Back.Components.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests;

public sealed class GitProcessOutputStreamTests
{
    [Fact]
    public async Task DisposeAsync_AllowsAHealthyProcessToExitNaturally()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"aetheus-git-stream-{Guid.NewGuid():N}.txt");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var process = Process.Start(CreateDelayedMarkerProcess(marker));
        Assert.NotNull(process);

        try
        {
            // Explicit, generous grace. What this test proves is "a healthy process is awaited rather
            // than killed" - not how fast the OS can start an interpreter. On the 2 s production default
            // it raced powershell.exe startup and failed under load with the marker never written. The
            // assertion keeps its full discriminating power: a premature kill still leaves no marker.
            // The bound itself is proved separately by DisposeAsync_KillsBlockedProcessAfterBoundedGracePeriod.
            var stream = new GitProcessOutputStream(
                process,
                timeout,
                NullLogger.Instance,
                "git-upload-pack",
                TimeSpan.FromSeconds(30));
            await stream.DisposeAsync();

            Assert.True(
                File.Exists(marker),
                "DisposeAsync killed a healthy process instead of letting it finish: the marker was never written.");
            Assert.Equal("done", File.ReadAllText(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task DisposeAsync_KillsBlockedProcessAfterBoundedGracePeriod()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var process = Process.Start(CreateBlockedProcess());
        Assert.NotNull(process);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exited.TrySetResult();
        var stream = new GitProcessOutputStream(
            process,
            timeout,
            NullLogger.Instance,
            "git-upload-pack",
            TimeSpan.FromMilliseconds(100));

        var stopwatch = Stopwatch.StartNew();
        await stream.DisposeAsync();
        stopwatch.Stop();

        await exited.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        // Lower bound: the 100 ms grace was actually honoured, the process was not killed on sight.
        // Upper bound: the wait is BOUNDED - a blocked process would otherwise hold dispose forever.
        // It is deliberately far above the grace period: the property under test is "finite", and a
        // tight ceiling only measured scheduler latency on a loaded machine. The code bounds its two
        // waits (grace, then grace after the kill), not Process.Kill(entireProcessTree) itself, which
        // the OS can hold while it enumerates the tree: 12.5 s on 2026-10-04 with three suites running.
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Dispose_AfterCancellation_DoesNotBlockCallingThread()
    {
        using var timeout = new CancellationTokenSource();
        using var process = Process.Start(CreateBlockedProcess());
        Assert.NotNull(process);
        var stream = new GitProcessOutputStream(
            process,
            timeout,
            NullLogger.Instance,
            "git-upload-pack");
        timeout.Cancel();

        var stopwatch = Stopwatch.StartNew();
        stream.Dispose();
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Synchronous cancellation teardown blocked for {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Dispose_AfterCancellation_TerminatesOwnedProcessAsynchronously()
    {
        using var timeout = new CancellationTokenSource();
        using var process = Process.Start(CreateBlockedProcess());
        Assert.NotNull(process);
        // The stream OWNS this Process object: its background teardown calls Kill and then
        // process.Dispose(). Observing termination through that same object is therefore a race the
        // test loses under load - WaitForExitAsync/HasExited then throw "No process is associated with
        // this object", and Exited can be unregistered by the Dispose before the OS signals. So the
        // observation is made on the pid, outside that ownership. The assertion keeps its full power:
        // a teardown that fails to terminate leaves this 30 s sleeper alive and the wait below fails.
        var pid = process.Id;
        var stream = new GitProcessOutputStream(
            process,
            timeout,
            NullLogger.Instance,
            "git-upload-pack");
        timeout.Cancel();

        stream.Dispose();

        await WaitUntilProcessIsGoneAsync(pid, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Dispose_AfterAFailedGit_LogsTheExitCodeOfTheFinishedProcess()
    {
        var logger = new RecordingLogger<GitProcessOutputStream>();
        var (stream, inner) = await StartFinishedProcessAsync(exitCode: 3, logger);

        stream.Dispose();
        await WaitUntilReleasedAsync(inner);

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warning.Level);
        Assert.Contains("git git-upload-pack exited with code 3 after streaming", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_AfterASuccessfulGit_LogsNothing()
    {
        var logger = new RecordingLogger<GitProcessOutputStream>();
        var (stream, inner) = await StartFinishedProcessAsync(exitCode: 0, logger);

        stream.Dispose();
        await WaitUntilReleasedAsync(inner);

        Assert.Empty(logger.Entries);
    }

    private static async Task<(GitProcessOutputStream Stream, Stream Inner)> StartFinishedProcessAsync(
        int exitCode, RecordingLogger<GitProcessOutputStream> logger)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"exit {exitCode}");
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"exit {exitCode}");
        }

        var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var inner = process.StandardOutput.BaseStream;
        var stream = new GitProcessOutputStream(
            process, new CancellationTokenSource(), logger, "git-upload-pack");
        return (stream, inner);
    }

    /// <summary>The synchronous teardown runs on a worker: the output stream is released last, so a
    /// released stream means the failure check has already run. Released is observed without any I/O:
    /// on Linux the pipe keeps <c>CanRead</c> true after disposal, and a probing read could block
    /// forever (mirror run 1273 hung 30 min in this loop), so the pipe handle itself is inspected.</summary>
    private static async Task WaitUntilReleasedAsync(Stream inner)
    {
        var elapsed = Stopwatch.StartNew();
        while (!IsReleased(inner))
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), "The teardown worker never released the stream.");
            await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        }
    }

    private static bool IsReleased(Stream inner)
    {
        if (inner is not PipeStream pipe)
            return !inner.CanRead;
        try
        {
            return pipe.SafePipeHandle.IsClosed;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>
    /// Polls the OS for <paramref name="pid"/> until it is gone, using a handle the subject under test
    /// does not own. Fails with the elapsed budget rather than hanging.
    /// </summary>
    private static async Task WaitUntilProcessIsGoneAsync(int pid, TimeSpan budget)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < budget)
        {
            if (!IsRunning(pid)) return;
            await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Dispose left the owned process {pid} running after {budget.TotalSeconds:0} s.");
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var probe = Process.GetProcessById(pid);
            return !probe.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // the OS no longer knows this pid
        }
    }

    private static ProcessStartInfo CreateDelayedMarkerProcess(string marker)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "powershell.exe";
            startInfo.Environment["AETHEUS_TEST_MARKER"] = marker;
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "Start-Sleep -Milliseconds 100; [IO.File]::WriteAllText($env:AETHEUS_TEST_MARKER, 'done')");
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("sleep 0.1; printf done > \"$1\"");
            startInfo.ArgumentList.Add("aetheus-git-stream");
            startInfo.ArgumentList.Add(marker);
        }

        return startInfo;
    }

    private static ProcessStartInfo CreateBlockedProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "powershell.exe";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("sleep 30");
        }

        return startInfo;
    }
}
