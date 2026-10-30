// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Which pipeline run a blue-green transaction belongs to (decision of 2026-10-02). The control plane
/// names the run on every blue-green task, says whether that run holds the environment lock, and lists
/// the runs still in flight. A transaction another run left open is an orphan when that run can no
/// longer act on it: with the lock, no other run can be mid-deployment on the environment; without it,
/// the owner is an earlier run the control plane no longer counts as in flight (it failed, was
/// cancelled or crashed before its rollback ran). An orphan is undone before a new deployment starts,
/// instead of wedging every deployment until someone clears it by hand. When neither can be shown,
/// nothing is assumed and the new deployment is refused before it rebuilds the colour the open
/// transaction would fall back to.
/// </summary>
internal static class BlueGreenTransactionOwnership
{
    internal const string OwnerRunVariable = "AETHEUS_BG_OWNER_RUN";
    internal const string ExclusiveVariable = "AETHEUS_BG_EXCLUSIVE";

    /// <summary>Comma-separated ids of the runs the control plane counts as in flight when it created
    /// the task. Absent (an older control plane) means unknown, never "none in flight".</summary>
    internal const string ActiveRunsVariable = "AETHEUS_BG_ACTIVE_RUNS";

    internal static string? OwnerOf(IReadOnlyDictionary<string, string> envVars) =>
        envVars.GetValueOrDefault(OwnerRunVariable)?.Trim() is { Length: > 0 } owner ? owner : null;

    internal static bool HoldsEnvironmentLock(IReadOnlyDictionary<string, string> envVars) =>
        string.Equals(envVars.GetValueOrDefault(ExclusiveVariable)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    internal static bool IsOpen(BlueGreenJournal journal) =>
        journal.Exists && journal.State is BlueGreenJournal.Prepared or BlueGreenJournal.Switched;

    /// <summary>An open transaction this run did not start. One written before owners were recorded counts.</summary>
    internal static bool IsAnotherRuns(BlueGreenJournal journal, string? owner) =>
        IsOpen(journal) && !string.Equals(journal.OwnerRun, owner, StringComparison.Ordinal);

    /// <summary>
    /// The run that opened the transaction is over. Only an EARLIER run qualifies: run ids grow, so a run
    /// started after the control plane listed the runs in flight has a higher id than this one, and its
    /// absence from the list proves nothing. An unrecorded owner, or no list at all, is never taken as
    /// ended.
    /// </summary>
    internal static bool HasEnded(string? openedBy, IReadOnlyDictionary<string, string> envVars)
    {
        if (!int.TryParse(openedBy, NumberStyles.None, CultureInfo.InvariantCulture, out var ownerRun)
            || !int.TryParse(OwnerOf(envVars), NumberStyles.None, CultureInfo.InvariantCulture, out var thisRun)
            || ownerRun >= thisRun
            || envVars.GetValueOrDefault(ActiveRunsVariable) is not { } listed)
            return false;
        return !listed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(openedBy!, StringComparer.Ordinal);
    }

    /// <summary>Another run's open transaction that this run may undo: see the class summary.</summary>
    internal static bool IsOrphan(BlueGreenJournal journal, IReadOnlyDictionary<string, string> envVars) =>
        HoldsEnvironmentLock(envVars) || HasEnded(journal.OwnerRun, envVars);

    /// <summary>
    /// Before a deployment rebuilds a colour: refuses when another run's transaction is open and is not
    /// shown to be an orphan, undoes it when it is. Null when the deployment may go on.
    /// </summary>
    internal static async Task<ExecutorResult?> SettleAnotherRunsTransactionAsync(
        BlueGreenJournal journal, IReadOnlyDictionary<string, string> envVars,
        Func<Task<ExecutorResult>> undoOrphan, Func<string, TaskLogLevel, Task> onOutput)
    {
        if (!IsAnotherRuns(journal, OwnerOf(envVars))) return null;
        var orphanRun = journal.OwnerRun ?? "unknown";
        if (!IsOrphan(journal, envVars))
        {
            await onOutput(
                $"Run {orphanRun} left a deployment transaction open (state={journal.State}); its rollback must run "
                + "before another deployment rebuilds the colour it would fall back to.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }
        var why = HoldsEnvironmentLock(envVars)
            ? "this run holds the environment"
            : $"run {orphanRun} is no longer in flight";
        await onOutput(
            $"Run {orphanRun} ended with its deployment transaction open (state={journal.State}); {why}, "
            + "so that transaction is an orphan and is undone first.",
            TaskLogLevel.Warning).ConfigureAwait(false);
        var recovery = await undoOrphan().ConfigureAwait(false);
        return recovery.ExitCode == 0 ? null : recovery;
    }

    /// <summary>A rollback refuses a transaction another run opened. Null when it is this run's, or unrecorded.</summary>
    internal static async Task<ExecutorResult?> RefuseAnotherRunsRollbackAsync(
        BlueGreenJournal journal, IReadOnlyDictionary<string, string> envVars, Func<string, TaskLogLevel, Task> onOutput)
    {
        if (journal.OwnerRun is not { } openedBy || OwnerOf(envVars) is not { } owner
            || string.Equals(openedBy, owner, StringComparison.Ordinal))
            return null;
        await onOutput(
            $"The open deployment transaction belongs to run {openedBy}, not to this run ({owner}); it is not this run's to undo.",
            TaskLogLevel.Error).ConfigureAwait(false);
        return new ExecutorResult(1, false);
    }

    /// <summary>
    /// A retirement refuses a transaction another run opened unless that run is shown to be over (see
    /// <see cref="IsOrphan"/>): retiring a live run's first deployment would stop the colour it is still
    /// validating. Null when it is this run's, unrecorded, or an orphan.
    /// </summary>
    internal static async Task<ExecutorResult?> RefuseAnotherLiveRunsRetireAsync(
        BlueGreenJournal journal, IReadOnlyDictionary<string, string> envVars, Func<string, TaskLogLevel, Task> onOutput)
    {
        if (journal.OwnerRun is not { } openedBy || OwnerOf(envVars) is not { } owner
            || string.Equals(openedBy, owner, StringComparison.Ordinal) || IsOrphan(journal, envVars))
            return null;
        await onOutput(
            $"The open deployment transaction belongs to run {openedBy}, which may still be running; this run ({owner}) "
            + "does not retire it.",
            TaskLogLevel.Error).ConfigureAwait(false);
        return new ExecutorResult(1, false);
    }
}
