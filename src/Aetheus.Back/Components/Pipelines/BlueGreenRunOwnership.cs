// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Tells the agent which run a blue-green step belongs to, whether that run holds the environment
/// lock (decision of 2026-10-02), and, for the steps that may undo another run's transaction
/// (<c>bluegreen-up</c>, <c>bluegreen-retire</c>), which runs are still in flight. The agent records the
/// run in the transaction journal: a rollback then undoes only its own run's deployment. With the
/// lock, or when the run that left a transaction open is an earlier run no longer in flight, that
/// transaction is an orphan the agent may undo before starting, instead of refusing every deployment
/// until someone clears it. Pipelines that declare no environment (the generic deployment template)
/// never hold the lock, so without the list a failed first deployment blocked every later one.
/// </summary>
internal static class BlueGreenRunOwnership
{
    internal const string OwnerRunVariable = "AETHEUS_BG_OWNER_RUN";
    internal const string ExclusiveVariable = "AETHEUS_BG_EXCLUSIVE";
    internal const string ActiveRunsVariable = "AETHEUS_BG_ACTIVE_RUNS";

    internal static async Task StampAsync(
        Dictionary<string, string> variables, int runId, IPipelineResourceLockRepository? locks,
        Func<CancellationToken, Task<List<int>>>? activeRuns, CancellationToken ct)
    {
        variables[OwnerRunVariable] = runId.ToString(CultureInfo.InvariantCulture);
        if (locks is not null && await locks.HoldsAnyAsync(runId, ct).ConfigureAwait(false))
            variables[ExclusiveVariable] = "true";
        if (activeRuns is not null)
            variables[ActiveRunsVariable] = string.Join(
                ',', (await activeRuns(ct).ConfigureAwait(false)).Order().Select(id => id.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>The steps that may find another run's open transaction and undo it when it is an orphan.</summary>
    internal static bool MayUndoAnotherRun(OperationKind operation) =>
        operation is OperationKind.BlueGreenUp or OperationKind.BlueGreenRetire;
}
