// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Pipelines;

internal sealed class PipelineRunStatusRepository(AppDbContext db, ILogger logger)
{
    private const string StageCollectionPrefix = "Collect Artifacts (";
    private const string StageCollectionSuffix = ")";

    /// <summary>The name of the collection a stage's <c>artifacts:</c> dispatches once the stage is done.
    /// That task carries no step run, so its name is what ties it to the stage it collects.</summary>
    internal static string StageArtifactCollectionTaskName(string stageName) =>
        StageCollectionPrefix + stageName + StageCollectionSuffix;

    public async Task<PipelineStatus> ResolveTerminalStatusAsync(
        int runId,
        PipelineStatus requestedStatus,
        CancellationToken ct)
    {
        // BACK-019: an artifact the run declared but never collected is a failure, whatever its steps
        // did. Checked outside the safety net's catch: a Success must never be granted on a failed query.
        if (requestedStatus == PipelineStatus.Success)
            return await HasFailedArtifactCollectionAsync(runId, ct).ConfigureAwait(false)
                ? PipelineStatus.Failed
                : requestedStatus;
        if (requestedStatus != PipelineStatus.Failed)
            return requestedStatus;

        try
        {
            var hasFailedStep = await db.PipelineStepRuns
                .AnyAsync(step => step.PipelineRunId == runId
                    && (step.Status == TaskExecutionStatus.Failed
                        || step.Status == TaskExecutionStatus.Timeout), ct)
                .ConfigureAwait(false);
            var hasFailedArtifactCollection = await HasFailedArtifactCollectionAsync(runId, ct).ConfigureAwait(false);
            var hasSucceededStep = await db.PipelineStepRuns
                .AnyAsync(step => step.PipelineRunId == runId
                    && !step.IsSystem
                    && step.Status == TaskExecutionStatus.Success, ct)
                .ConfigureAwait(false);

            logger.LogDebug(
                "Run {RunId} safety net: hasFailedStep={HasFailedStep} hasFailedArtifactCollection={HasFailedArtifactCollection} hasSucceededStep={HasSucceededStep}",
                runId,
                hasFailedStep,
                hasFailedArtifactCollection,
                hasSucceededStep);

            return !hasFailedStep && !hasFailedArtifactCollection && hasSucceededStep
                ? PipelineStatus.Success
                : requestedStatus;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Safety net query failed for run {RunId}", runId);
            return requestedStatus;
        }
    }

    /// <summary>
    /// BACK-019: an artifact the run declared and could not collect fails it. Only the run's current word
    /// on that artifact counts:
    /// <list type="bullet">
    /// <item>a step's collection (<c>type: collect-artifacts</c>) is judged on its latest attempt, while
    /// that step is still failed, and never when the step may fail (<c>continue_on_error</c>): a retried
    /// step that collected on its second attempt, or an optional one, does not fail the run;</item>
    /// <item>a stage's collection (<c>artifacts:</c>) is judged on its latest attempt, unless the stage is
    /// being replayed by a retry (a step of it not terminal yet), which collects it again.</item>
    /// </list>
    /// </summary>
    public Task<bool> HasFailedArtifactCollectionAsync(int runId, CancellationToken ct) =>
        db.Tasks.AnyAsync(task => task.PipelineRunId == runId
            && task.Operation == OperationKind.PipelineCollectArtifacts
            && (task.Status == TaskExecutionStatus.Failed
                || task.Status == TaskExecutionStatus.Timeout
                || task.Status == TaskExecutionStatus.Cancelled)
            && (task.PipelineStepRunId != null
                ? !db.Tasks.Any(later => later.PipelineStepRunId == task.PipelineStepRunId
                        && later.Operation == OperationKind.PipelineCollectArtifacts
                        && later.Id > task.Id)
                    && db.PipelineStepRuns.Any(step => step.Id == task.PipelineStepRunId
                        && !step.ContinueOnError
                        && (step.Status == TaskExecutionStatus.Failed
                            || step.Status == TaskExecutionStatus.Timeout
                            || step.Status == TaskExecutionStatus.Cancelled))
                : !db.Tasks.Any(later => later.PipelineRunId == runId
                        && later.PipelineStepRunId == null
                        && later.Operation == OperationKind.PipelineCollectArtifacts
                        && later.Name == task.Name
                        && later.Id > task.Id)
                    && !db.PipelineStepRuns.Any(step => step.PipelineRunId == runId
                        && StageCollectionPrefix + step.StageName + StageCollectionSuffix == task.Name
                        && (step.Status == TaskExecutionStatus.Pending
                            || step.Status == TaskExecutionStatus.Assigned
                            || step.Status == TaskExecutionStatus.Running))), ct);

    /// <summary>
    /// The stages whose latest collection failed. A retry replays them: their artifact only exists if
    /// the stage runs again, a cleanup having taken the workspace it was collected from.
    /// </summary>
    internal static async Task<List<string>> GetStagesWithFailedCollectionAsync(
        AppDbContext db, int runId, CancellationToken ct)
    {
        var failedNames = await db.Tasks.AsNoTracking()
            .Where(task => task.PipelineRunId == runId
                && task.PipelineStepRunId == null
                && task.Operation == OperationKind.PipelineCollectArtifacts
                && (task.Status == TaskExecutionStatus.Failed
                    || task.Status == TaskExecutionStatus.Timeout
                    || task.Status == TaskExecutionStatus.Cancelled)
                && !db.Tasks.Any(later => later.PipelineRunId == runId
                    && later.PipelineStepRunId == null
                    && later.Operation == OperationKind.PipelineCollectArtifacts
                    && later.Name == task.Name
                    && later.Id > task.Id))
            .Select(task => task.Name)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (failedNames.Count == 0) return [];
        var stageNames = await db.PipelineStepRuns.AsNoTracking()
            .Where(step => step.PipelineRunId == runId)
            .Select(step => step.StageName)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        return stageNames
            .Where(stage => failedNames.Contains(StageArtifactCollectionTaskName(stage), StringComparer.Ordinal))
            .ToList();
    }
}
