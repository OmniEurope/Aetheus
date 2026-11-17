// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>Recette R-24: which runs of the list's live runs appear in the "running now" table, and in what order.
/// Urgency first: a run waiting for a person (an approval, a resume) leads, then the longest-running first.</summary>
internal static class PipelineRunningNowRows
{
    /// <summary>Whether the run is unfinished and showing in the table: running, waiting for a runner, waiting for
    /// an approval, or parked awaiting a resume.</summary>
    public static bool IsShown(PipelineStatus status) =>
        status is PipelineStatus.Running or PipelineStatus.Pending
            or PipelineStatus.WaitingForApproval or PipelineStatus.AwaitingResume;

    /// <summary>True for the runs that wait for a person to act, which is what the table puts first.</summary>
    public static bool NeedsAPerson(PipelineStatus status) =>
        status is PipelineStatus.WaitingForApproval or PipelineStatus.AwaitingResume;

    public static IReadOnlyList<PipelineRunTableItem> From(IEnumerable<PipelineRunDto> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return [.. runs
            .Where(run => IsShown(run.Status))
            .Select(PipelineRunTableItem.FromRun)
            .OrderByDescending(run => NeedsAPerson(run.Status))
            .ThenBy(run => run.StartedAt)];
    }
}
