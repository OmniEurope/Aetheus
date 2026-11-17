// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Diagnostics;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

[Collection(ProcessSpawningTestCollection.Name)]
public sealed class ExecutorProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_Success_PublishesResourceMetrics()
    {
        var health = new AgentRuntimeHealth(TimeProvider.System);
        var runner = new ExecutorProcessRunner(
            health,
            NullLogger<ExecutorProcessRunner>.Instance);
        var output = new ConcurrentQueue<string>();

        var result = await runner.RunAsync(
            BuildSuccessfulCommand(),
            timeoutSeconds: 10,
            (line, _) =>
            {
                output.Enqueue(line);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains(output, line => line.StartsWith(
            "##aetheus[pipelinemetric key=step.cpu;type=Duration;unit=s]",
            StringComparison.Ordinal));
        Assert.Equal(0, health.ActiveProcessCount);
        Assert.True(health.IsIdle);
    }

    // R-36: the real lines of docker compose, jscpd and trivy arrive on a real stderr pipe; only the line that
    // states an error is an Error, the others stay visible without being red, and none is lost.
    [Fact]
    public async Task RunAsync_ToolProgressOnStderr_IsNotLoggedAsAnError()
    {
        var runner = new ExecutorProcessRunner(
            new AgentRuntimeHealth(TimeProvider.System),
            NullLogger<ExecutorProcessRunner>.Instance);
        var output = new ConcurrentQueue<(string Line, TaskLogLevel Level)>();

        var result = await runner.RunAsync(
            BuildStderrCommand(),
            timeoutSeconds: 10,
            (line, level) =>
            {
                output.Enqueue((line, level));
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var levels = output
            .GroupBy(entry => entry.Line.Trim())
            .ToDictionary(group => group.Key, group => group.First().Level);
        Assert.Equal(TaskLogLevel.Warning, levels["[stderr] Volume aetheus-qa-rollback-2848-x Creating"]);
        Assert.Equal(TaskLogLevel.Warning, levels["[stderr] Using config from .jscpd.json"]);
        Assert.Equal(TaskLogLevel.Info, levels["[stderr] INFO Detected OS family=ubuntu"]);
        Assert.Equal(TaskLogLevel.Error, levels["ERROR the scan failed"]);
    }

    [Fact]
    public async Task RunAsync_ExternalCancellation_TerminatesAndWaitsForDescendantTree()
    {
        var startedMarker = Path.Combine(Path.GetTempPath(), $"aetheus-started-{Guid.NewGuid():N}.marker");
        var health = new AgentRuntimeHealth(TimeProvider.System);
        var runner = new ExecutorProcessRunner(
            health,
            NullLogger<ExecutorProcessRunner>.Instance);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var descendantId = 0;

        try
        {
            var execution = runner.RunAsync(
                BuildDescendantCommand(startedMarker),
                timeoutSeconds: RunnerTimeoutSeconds,
                static (_, _) => Task.CompletedTask,
                cancellation.Token);

            // The descendant announces itself with its process id: cancelling before it exists would cancel a
            // bare root and prove nothing about the tree. PowerShell takes 3.6 to 7.1 s to start on a loaded
            // machine, and the descendant lives for minutes, so a stall of the test thread (measured at over
            // 3 s under load) cannot let it finish by itself before the cancellation.
            var startupWait = Stopwatch.StartNew();
            while (!TryReadProcessId(startedMarker, out descendantId)
                && !execution.IsCompleted
                && startupWait.Elapsed < TimeSpan.FromSeconds(60))
                await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
            var startedAfter = startupWait.ElapsedMilliseconds;
            Assert.True(descendantId > 0, $"The descendant did not announce itself {startedAfter} ms after the start.");
            Assert.False(execution.IsCompleted, "The descendant command exited before cancellation.");
            var countedAtStart = health.ActiveProcessCount;
            Assert.True(countedAtStart == 1,
                $"Expected the root command alone to be counted {startedAfter} ms after the start, got "
                + $"{countedAtStart}: " + string.Join(" | ", health.DescribeActiveProcesses()));
            Assert.False(health.IsIdle);

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

            // A360-38: this asserted an exact 0 the instant the task faulted, and failed intermittently
            // under load with a non-zero count. The production side is not wrong: ActiveProcessCount asks
            // the OS, and IsRunning deliberately answers "still running" on a Win32Exception, because
            // claiming idle while processes survive is the dangerous direction for a health signal that
            // gates accepting work. Reaping a descendant TREE is asynchronous by nature, so the contract
            // is "converges to zero", not "is zero on this exact instruction". The wait is bounded, so a
            // tree that genuinely survives still fails the test - it just fails on the real defect.
            var settle = Stopwatch.StartNew();
            while (health.ActiveProcessCount > 0 && settle.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

            Assert.True(health.ActiveProcessCount == 0,
                $"Still counted {settle.ElapsedMilliseconds} ms after cancellation: "
                + string.Join(" | ", health.DescribeActiveProcesses()));
            Assert.True(health.IsIdle);

            // The survivor is looked up by its id: it was seen only by a marker it wrote 3 s after its
            // start, a timed witness that a stalled test thread read too early or too late.
            var survivor = Stopwatch.StartNew();
            while (IsRunning(descendantId) && survivor.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            Assert.False(
                IsRunning(descendantId),
                $"The descendant (PID {descendantId}) survived external cancellation: still running {survivor.ElapsedMilliseconds} ms "
                + $"after the settle (it was seen running {startedAfter} ms after the start, {countedAtStart} process(es) counted then).");
        }
        finally
        {
            File.Delete(startedMarker);
        }
    }

    // The test waits up to 60 s for the descendant to announce itself, so the runner's own timeout must be
    // longer: at 30 s it ended a slow start by itself (a timed out run, not a cancelled one) when the
    // machine was starved, measured in 22 of 150 runs with 40 runs in parallel. And the descendant outlives
    // that timeout: only the cancellation ends it.
    private const int RunnerTimeoutSeconds = 120;
    private const int DescendantLifetimeSeconds = 300;

    private static bool TryReadProcessId(string marker, out int processId)
    {
        processId = 0;
        try
        {
            return File.Exists(marker) && int.TryParse(File.ReadAllText(marker), out processId) && processId > 0;
        }
        catch (IOException)
        {
            return false; // still being written
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static ProcessStartInfo BuildDescendantCommand(string startedMarker)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            // Encoded, so no quote crosses the cmd -> powershell boundary: a quoted -Command reached
            // PowerShell as a string literal, which it printed instead of running, so the descendant
            // exited about 0.8 s after its start and never slept.
            var windowsStarted = startedMarker.Replace("'", "''", StringComparison.Ordinal);
            var script = $"[IO.File]::WriteAllText('{windowsStarted}', [string]$PID); Start-Sleep -Seconds {DescendantLifetimeSeconds}";
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            var child = $"powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}";
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(child);
            return startInfo;
        }

        var linuxStarted = startedMarker.Replace("'", "'\\''", StringComparison.Ordinal);
        startInfo.FileName = "/bin/sh";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            $"/bin/sh -c 'printf %s \"$$\" > \"{linuxStarted}\"; sleep {DescendantLifetimeSeconds}' & wait");
        return startInfo;
    }

    private static ProcessStartInfo BuildStderrCommand()
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(
                "(echo Volume aetheus-qa-rollback-2848-x Creating)1>&2 & (echo Using config from .jscpd.json)1>&2"
                + " & (echo INFO Detected OS family=ubuntu)1>&2 & (echo ERROR the scan failed)1>&2");
            return startInfo;
        }

        startInfo.FileName = "/bin/sh";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            "printf '%s\\n' 'Volume aetheus-qa-rollback-2848-x Creating' 'Using config from .jscpd.json'"
            + " 'INFO Detected OS family=ubuntu' 'ERROR the scan failed' >&2");
        return startInfo;
    }

    private static ProcessStartInfo BuildSuccessfulCommand()
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("exit 0");
            return startInfo;
        }

        startInfo.FileName = "/bin/sh";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exit 0");
        return startInfo;
    }
}
