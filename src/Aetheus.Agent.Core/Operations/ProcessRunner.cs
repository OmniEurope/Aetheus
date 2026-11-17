// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// F-32: shared launch/stream/timeout helper for typed operation executors. Centralises the
/// bookkeeping (linked CTS, output streaming, kill-on-timeout) so each operation executor
/// only needs to assemble its <c>ArgumentList</c>.
/// </summary>
internal static class ProcessRunner
{
    // How long a killed process tree is given to exit before the runner stops waiting for it.
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    public static async Task<ExecutorResult> RunAsync(
        ProcessStartInfo psi,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        ILogger logger,
        CancellationToken cancellationToken,
        string? standardInput = null,
        Func<Process, ILogger, Task<bool>>? stopTree = null)
    {
        // When a secret (e.g. a mail admin password) must reach the child process without appearing
        // on the argv / process list, the caller passes it here and we pipe it to stdin, then close.
        if (standardInput is not null)
            psi.RedirectStandardInput = true;

        // ACORE-001: a sudo command runs in a process group of its own, so its root children can be stopped and
        // their exit proven although the agent can neither see nor kill them (SudoProcessGroup).
        stopTree ??= OperatingSystem.IsLinux() && SudoProcessGroup.Isolate(psi)
            ? SudoProcessGroup.StopAsync
            : (p, l) => StopTreeAsync(p, l);

        using var process = new Process { StartInfo = psi };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Hardening (#38): a missing binary (e.g. docker.exe not on PATH, sc.exe denied) used
            // to surface as an unhandled Win32Exception and crash the polling loop. Convert into
            // a structured failure so the task is marked Failed and the agent keeps polling.
            await onOutput($"Failed to launch '{psi.FileName}': {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            logger.LogError(ex, "Process launch failed for {File}", psi.FileName);
            return new ExecutorResult(-1, false);
        }

        // From here on the child is ours to stop: a timeout, a task cancellation or any other failure
        // kills its whole tree before the runner returns, so an apt, a deploy restart or a certbot run
        // never keeps acting after the task (and the lock it held) is gone.
        var exited = false;
        var treeStopped = true;
        try
        {
            var stdout = ExecutorHelper.StreamOutputAsync(process.StandardOutput, TaskLogLevel.Info, onOutput, timeoutCts.Token, logger);
            // stderr is an output channel, not a semantic failure level: many successful tools use it for
            // progress and diagnostics. Preserve the channel explicitly in the message and reserve Error for
            // the exit-code proof below.
            var stderr = ExecutorHelper.StreamOutputAsync(
                process.StandardError,
                TaskLogLevel.Warning,
                (line, level) => onOutput($"[stderr] {line.TrimEnd()}", level),
                timeoutCts.Token,
                logger);

            // A child that never reads its stdin blocks the write, and a pipe write does not always
            // observe a token: the wait is bounded here, and the kill below unblocks the write itself.
            if (standardInput is not null)
                await WriteStandardInputAsync(process, standardInput).WaitAsync(timeoutCts.Token).ConfigureAwait(false);

            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            exited = true;
            await onOutput(
                $"[process] exit-code={process.ExitCode}",
                process.ExitCode == 0 ? TaskLogLevel.Info : TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(process.ExitCode, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Operation timed out after {Timeout}s, killing", timeoutSeconds);
            return new ExecutorResult(-1, true);
        }
        finally
        {
            if (!exited)
                treeStopped = await stopTree(process, logger).ConfigureAwait(false);

            // A cancelled task is acknowledged as stopped only with the proof above; without it the agent
            // must not tell the control plane that the task stopped (R-12, same contract as
            // ExecutorProcessRunner). A timeout keeps its own result: nobody acknowledges it.
            if (!treeStopped && cancellationToken.IsCancellationRequested)
                throw new ProcessTreeNotTerminatedException(cancellationToken);
        }
    }

    private static async Task WriteStandardInputAsync(Process process, string standardInput)
    {
        try
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory()).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The child exited before consuming stdin (e.g. `sudo -n` rejected because the grant is
            // not provisioned), or was killed while the write was blocked. Its exit code + stderr are
            // captured by the caller; this broken pipe is a symptom, not the real error.
        }
    }

    /// <summary>Runs a read-only query (no shell, argv only) and hands back its result with every output line
    /// it printed, stderr lines prefixed <c>[stderr]</c>, for executors that parse an answer rather than
    /// stream it to the task log.</summary>
    public static async Task<(ExecutorResult Result, IReadOnlyList<string> Lines)> CaptureAsync(
        string fileName, IReadOnlyList<string> argv, int timeoutSeconds, ILogger logger, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in argv) psi.ArgumentList.Add(arg);
        var lines = new List<string>();
        var result = await RunAsync(psi, timeoutSeconds, (line, _) =>
        {
            lock (lines) lines.Add(line);
            return Task.CompletedTask;
        }, logger, ct).ConfigureAwait(false);
        return (result, lines);
    }

    internal static async Task<bool> StopTreeAsync(
        Process process, ILogger logger, Action<bool>? kill = null, TimeSpan? killWait = null)
    {
        // Runs in the finally of RunAsync: a refusal (EPERM on a sudo child) is logged, never thrown over
        // the task's real outcome. What is returned is the proof: true only when the process and every
        // descendant seen before the kill were observed to have exited, so a refused kill of a privileged
        // child (or a tree that outlives the wait) is never taken for a stop.
        if (ExecutorProcessRunner.HasExited(process)) return true;

        var descendants = ProcessTreeSnapshot.CaptureDescendants(process.Id);
        try
        {
            ProcessTreeTerminator.Kill(kill ?? process.Kill, process.Id, logger);

            var wait = killWait ?? KillWait;
            using var waitCts = new CancellationTokenSource(wait);
            var exited = await Task.WhenAll(descendants.Prepend(process)
                    .Select(candidate => ExecutorProcessRunner.WaitForExitAsync(candidate, waitCts.Token)))
                .ConfigureAwait(false);
            if (exited.All(static done => done)) return true;

            logger.LogCritical(
                "Process tree rooted at PID {Pid} could not be proven terminated within {Wait}s of being killed",
                process.Id, wait.TotalSeconds);
            return false;
        }
        finally
        {
            foreach (var descendant in descendants)
                descendant.Dispose();
        }
    }
}
