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

    [Fact]
    public async Task RunAsync_ExternalCancellation_TerminatesAndWaitsForDescendantTree()
    {
        var survivorMarker = Path.Combine(Path.GetTempPath(), $"aetheus-survivor-{Guid.NewGuid():N}.marker");
        var health = new AgentRuntimeHealth(TimeProvider.System);
        var runner = new ExecutorProcessRunner(
            health,
            NullLogger<ExecutorProcessRunner>.Instance);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        try
        {
            var execution = runner.RunAsync(
                BuildDescendantCommand(survivorMarker),
                timeoutSeconds: 30,
                static (_, _) => Task.CompletedTask,
                cancellation.Token);

            var startupWait = Stopwatch.StartNew();
            while (health.ActiveProcessCount == 0
                && !execution.IsCompleted
                && startupWait.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
            Assert.False(execution.IsCompleted, "The descendant command exited before cancellation.");
            Assert.Equal(1, health.ActiveProcessCount);
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

            Assert.Equal(0, health.ActiveProcessCount);
            Assert.True(health.IsIdle);

            await Task.Delay(TimeSpan.FromSeconds(WitnessDelaySeconds + 1), TestContext.Current.CancellationToken);
            Assert.False(
                File.Exists(survivorMarker),
                "A descendant survived external cancellation and wrote its marker.");
        }
        finally
        {
            File.Delete(survivorMarker);
        }
    }

    // The descendant must outlive the cancel-to-check window by seconds, never by tens of
    // milliseconds, or a loaded CI machine lets it either finish early or hide a surviving tree.
    private const int WitnessDelaySeconds = 3;

    private static ProcessStartInfo BuildDescendantCommand(string survivorMarker)
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
            var windowsSurvivor = survivorMarker.Replace("'", "''", StringComparison.Ordinal);
            var child = "powershell.exe -NoLogo -NoProfile -NonInteractive -Command "
                + $"\"Start-Sleep -Seconds {WitnessDelaySeconds}; "
                + $"[IO.File]::WriteAllText('{windowsSurvivor}', 'survived')\"";
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(child);
            return startInfo;
        }

        var linuxSurvivor = survivorMarker.Replace("'", "'\\''", StringComparison.Ordinal);
        startInfo.FileName = "/bin/sh";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            $"/bin/sh -c 'sleep {WitnessDelaySeconds}; printf survived > \"{linuxSurvivor}\"' & wait");
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
