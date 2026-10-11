// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel;
using System.Diagnostics;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Executors;

public sealed class ExecutorProcessRunner(
    AgentRuntimeHealth runtimeHealth,
    ILogger<ExecutorProcessRunner> logger)
{
    private static readonly TimeSpan TerminationWaitTimeout = TimeSpan.FromSeconds(10);

    public async Task<ExecutorResult> RunAsync(
        ProcessStartInfo startInfo,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken,
        Func<StreamWriter, CancellationToken, Task>? writeStandardInput = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(onOutput);

        using var process = new Process { StartInfo = startInfo };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        process.Start();
        var activity = runtimeHealth.TrackProcess(process);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
        var resourceMonitor = ProcessResourceMonitor.MonitorAsync(process, monitorCancellation.Token);
        try
        {
            var stdoutTask = ExecutorHelper.StreamOutputAsync(
                process.StandardOutput, TaskLogLevel.Info, onOutput, timeoutCts.Token, logger);
            var stderrTask = ExecutorHelper.StreamOutputAsync(
                process.StandardError, TaskLogLevel.Error, onOutput, timeoutCts.Token, logger);
            var standardInputTask = writeStandardInput is null
                ? Task.CompletedTask
                : WriteStandardInputAsync(process.StandardInput, writeStandardInput, timeoutCts.Token);

            await Task.WhenAll(
                    stdoutTask,
                    stderrTask,
                    standardInputTask,
                    process.WaitForExitAsync(timeoutCts.Token))
                .ConfigureAwait(false);
            return new ExecutorResult(process.ExitCode, false);
        }
        catch (OperationCanceledException)
        {
            var externallyCancelled = cancellationToken.IsCancellationRequested;
            if (externallyCancelled)
            {
                logger.LogWarning("Process execution was cancelled; terminating its process tree");
            }
            else
            {
                logger.LogWarning(
                    "Process timed out after {Timeout}s; terminating its process tree",
                    timeoutSeconds);
            }

            var terminated = await TerminateProcessTreeAsync(process).ConfigureAwait(false);
            if (!terminated)
            {
                logger.LogCritical(
                    "Process tree rooted at PID {ProcessId} could not be proven terminated; agent remains busy",
                    process.Id);
            }

            if (externallyCancelled)
                cancellationToken.ThrowIfCancellationRequested();

            return new ExecutorResult(-1, true);
        }
        finally
        {
            await monitorCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                var resourceUsage = await resourceMonitor.ConfigureAwait(false);
                await ProcessResourceMonitor.PublishAsync(resourceUsage, onOutput).ConfigureAwait(false);
            }
            finally
            {
                if (HasExited(process))
                    activity.Dispose();
            }
        }
    }

    private static async Task WriteStandardInputAsync(
        StreamWriter standardInput,
        Func<StreamWriter, CancellationToken, Task> writeStandardInput,
        CancellationToken cancellationToken)
    {
        try
        {
            await writeStandardInput(standardInput, cancellationToken).ConfigureAwait(false);
            await standardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            standardInput.Close();
        }
    }

    private async Task<bool> TerminateProcessTreeAsync(Process process)
    {
        var descendants = ProcessTreeSnapshot.CaptureDescendants(process.Id);
        var descendantRegistrations = descendants
            .Select(runtimeHealth.TrackProcess)
            .ToArray();
        // A tree it may not kill whole (EPERM on a `sudo -n` child, an AggregateException) is logged and
        // the root killed alone; the wait below still reports whether everything exited.
        ProcessTreeTerminator.Kill(process, logger);

        try
        {
            using var terminationCts = new CancellationTokenSource(TerminationWaitTimeout);
            var processes = descendants.Prepend(process);
            var terminated = await Task.WhenAll(
                    processes.Select(candidate => WaitForExitAsync(candidate, terminationCts.Token)))
                .ConfigureAwait(false);
            return terminated.All(static exited => exited);
        }
        finally
        {
            for (var index = 0; index < descendants.Count; index++)
            {
                if (HasExited(descendants[index]))
                    descendantRegistrations[index].Dispose();
                descendants[index].Dispose();
            }
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HasExited(process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return HasExited(process);
        }

        return HasExited(process);
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }
}
