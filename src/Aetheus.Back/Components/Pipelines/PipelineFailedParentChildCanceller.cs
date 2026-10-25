// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-31: when a parent run fails, the runs its trigger steps started that are still in flight stop
/// with it and end <c>Cancelled</c>, instead of running on for a result nobody waits for any more
/// (a failed nightly kept its sibling sub-runs busy for tens of minutes). The cancellation goes through
/// <see cref="IPipelineRunService.CancelRunAsync"/>, so the cascade is the user-cancel one: audited,
/// depth-first through the trigger tree, with the always() and rollback stages preserved.
/// </summary>
public sealed class PipelineFailedParentChildCanceller(
    IPipelineRepository repo,
    IPipelineRunService runService,
    ILogger<PipelineFailedParentChildCanceller> logger)
{
    /// <returns>The number of child runs that were still in flight and are now being cancelled.</returns>
    public async Task<int> CancelRunningChildrenAsync(int failedParentRunId, CancellationToken ct)
    {
        var childIds = await repo.GetTriggeredChildRunIdsAsync(failedParentRunId, ct).ConfigureAwait(false);
        var cancellation = PipelineRunCancellation.BySystem($"Parent run #{failedParentRunId} failed");

        var cancelled = 0;
        foreach (var childId in childIds)
        {
            // False when the child already ended: nothing was running, nothing to cancel.
            if (!await runService.CancelRunAsync(childId, cancellation, ct).ConfigureAwait(false))
                continue;

            cancelled++;
            logger.LogInformation(
                "Run #{ChildRunId} was cancelled because its parent run #{ParentRunId} failed", childId, failedParentRunId);
        }

        return cancelled;
    }
}
