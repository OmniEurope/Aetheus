// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel;
using System.Diagnostics;

namespace Aetheus.Agent.Core.Services;

/// <summary>
/// Kills a child process and its descendants, as far as the agent is allowed to.
/// <para>
/// A tree that holds processes of another user cannot be killed whole: a <c>sudo -n</c> child runs as
/// root, and signalling it from the agent's account fails with EPERM. <see cref="Process.Kill(bool)"/>
/// reports that as an <see cref="AggregateException"/> of <see cref="Win32Exception"/>s (a single
/// <see cref="Win32Exception"/> or an <see cref="UnauthorizedAccessException"/> on other paths). Left
/// uncaught in a runner's <c>finally</c>, it replaced the task's real outcome with an unhandled exception.
/// Here the refusal is logged, the root process is killed on its own when that is still possible, and
/// what could not be killed is logged as an error: it may keep running after its task ended.
/// </para>
/// </summary>
internal static class ProcessTreeTerminator
{
    /// <summary>Kills <paramref name="process"/> and its tree. Returns false when it had already exited.</summary>
    internal static bool Kill(Process process, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            if (process.HasExited) return false;
        }
        catch (InvalidOperationException)
        {
            return false; // never started, nothing to kill
        }
        return Kill(process.Kill, process.Id, logger);
    }

    /// <param name="kill">The kill primitive: <c>true</c> for the whole tree, <c>false</c> for the
    /// process alone (<see cref="Process.Kill(bool)"/>). A parameter so the refusal paths can be proved
    /// without a process owned by another user.</param>
    /// <param name="processId">Only used to name the process in the log.</param>
    /// <param name="logger">Receives the refusals.</param>
    /// <returns>False when the process had already exited, true otherwise (killed or not).</returns>
    internal static bool Kill(Action<bool> kill, int processId, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(kill);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            kill(true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false; // already exited
        }
        catch (Exception ex) when (IsRefusal(ex) || ex is NotSupportedException)
        {
            logger.LogWarning(ex,
                "Could not kill the whole process tree of PID {ProcessId} (a descendant may run as another user, e.g. under sudo); killing the process itself",
                processId);
        }

        try
        {
            kill(false);
        }
        catch (InvalidOperationException)
        {
            return false; // exited meanwhile
        }
        catch (Exception ex) when (IsRefusal(ex) || ex is NotSupportedException)
        {
            logger.LogError(ex,
                "Process {ProcessId} could not be killed; it may keep running after its task ended", processId);
        }
        return true;
    }

    private static bool IsRefusal(Exception ex) =>
        // Process.Kill(true) throws AggregateException only for a tree it could not fully terminate.
        ex is Win32Exception or UnauthorizedAccessException or AggregateException;
}
