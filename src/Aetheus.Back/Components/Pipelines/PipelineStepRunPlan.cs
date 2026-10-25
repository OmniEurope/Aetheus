// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Builds, in execution order, the step-run rows a freshly launched run starts with: the optional
/// System:Prepare step, one row per stage step and matrix leg, then the optional System:Cleanup step.
/// Pure: nothing is persisted here, the launcher tracks the rows it returns.
/// </summary>
internal static class PipelineStepRunPlan
{
    internal static List<PipelineStepRun> Build(PipelineRun run, PipelineRunPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(preparation);

        // System:Prepare (clone/prepare workspace) and System:Cleanup only make sense when at least one
        // step actually uses the workspace. A pure orchestration pipeline - every step is `type: trigger`,
        // which merely launches and waits on a child run - needs neither, so skip both. Keeps the run view
        // clean (no useless "Clone Repository"/"Cleanup") and lets the trigger steps stand on their own.
        var requiresWorkspace = PipelineRunPreparationService.RequiresWorkspace(preparation.Definition);
        var stepRuns = new List<PipelineStepRun>();
        if (requiresWorkspace)
        {
            stepRuns.Add(new PipelineStepRun
            {
                PipelineRunId = run.Id,
                StageName = PipelineRunHelpers.SystemPrepareStage,
                StepName = string.IsNullOrEmpty(preparation.RepositoryUrl) ? "Prepare Workspace" : "Clone Repository",
                Order = stepRuns.Count,
                IsSystem = true
            });
        }

        // One row per stage/step (with matrix expansion) - EffectiveStages was flattened once during
        // preparation (shared with the matrix validation). Artifact collection is dispatched post-stage
        // (in AdvanceStageAsync), not as an injected step - avoids the task claim concurrency bug.
        foreach (var stage in preparation.EffectiveStages)
            AddStageStepRuns(stepRuns, run, stage);

        // Inject system cleanup step (always runs) - only when the workspace was prepared above.
        if (requiresWorkspace)
        {
            stepRuns.Add(new PipelineStepRun
            {
                PipelineRunId = run.Id,
                StageName = PipelineRunHelpers.SystemCleanupStage,
                StepName = "Cleanup",
                Order = stepRuns.Count,
                IsSystem = true,
                ContinueOnError = true
            });
        }

        return stepRuns;
    }

    private static void AddStageStepRuns(List<PipelineStepRun> stepRuns, PipelineRun run, PipelineStageDefinition stage)
    {
        foreach (var leg in ExpandMatrix(stage.Matrix))
        {
            var legName = leg.Count > 0 ? string.Join("-", leg.Values) : null;

            foreach (var step in stage.Steps)
            {
                // "Run to end" continues past every failure; the step's own continue_on_error (or
                // advisory) keeps its meaning, only the steps without it are marked as forced.
                var declaresContinue = step.ContinueOnError || step.Advisory;
                stepRuns.Add(new PipelineStepRun
                {
                    PipelineRunId = run.Id,
                    StageName = stage.Name,
                    StepName = legName is not null ? $"{step.Name} [{legName}]" : step.Name,
                    Order = stepRuns.Count,
                    RetryCount = step.RetryCount,
                    // An advisory AI step never stops its dependents; the scheduler then also
                    // keeps its failure out of the run status (PipelineRunScheduler.AdvisoryStepKeys).
                    ContinueOnError = declaresContinue || run.RunToEnd,
                    ContinueOnErrorForced = run.RunToEnd && !declaresContinue,
                    MatrixLeg = legName,
                    GroupName = stage.Group
                });
            }
        }
    }
}
