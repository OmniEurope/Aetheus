// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Tasks;

public sealed class PipelineTaskLifecycleRepository(AppDbContext db, TimeProvider timeProvider)
    : IPipelineTaskLifecycle
{
    public Task CancelActiveTasksAsync(int pipelineRunId, CancellationToken ct = default) =>
        CancelAsync(
            pipelineRunId,
            [
                TaskExecutionStatus.Pending,
                TaskExecutionStatus.Assigned,
                TaskExecutionStatus.Running
            ],
            null,
            null,
            ct);

    public Task CancelPendingTasksExceptStagesAsync(
        int pipelineRunId,
        IReadOnlyCollection<string> preservedStages,
        CancellationToken ct = default) =>
        CancelAsync(
            pipelineRunId,
            [TaskExecutionStatus.Pending, TaskExecutionStatus.Assigned],
            preservedStages,
            null,
            ct);

    public Task CancelActiveTasksOfStepRunsAsync(
        int pipelineRunId,
        IReadOnlyCollection<int> stepRunIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stepRunIds);
        return stepRunIds.Count == 0
            ? Task.CompletedTask
            : CancelAsync(
                pipelineRunId,
                [TaskExecutionStatus.Pending, TaskExecutionStatus.Assigned, TaskExecutionStatus.Running],
                null,
                stepRunIds,
                ct);
    }

    private async Task CancelAsync(
        int pipelineRunId,
        IReadOnlyCollection<TaskExecutionStatus> statuses,
        IReadOnlyCollection<string>? preservedStages,
        IReadOnlyCollection<int>? onlyStepRunIds,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var query = db.Tasks.Where(task =>
            task.PipelineRunId == pipelineRunId && statuses.Contains(task.Status));
        if (onlyStepRunIds is not null)
        {
            var stepRunIds = onlyStepRunIds.ToArray();
            query = query.Where(task => task.PipelineStepRunId != null && stepRunIds.Contains(task.PipelineStepRunId.Value));
        }
        if (preservedStages is not null)
        {
            var preserved = preservedStages.ToArray();
            // Post-stage orchestration tasks (for example artifact collection) deliberately have no
            // PipelineStepRun. They cannot belong to a preserved always() stage, so cancellation must
            // include them; otherwise a cancelled run can remain wedged forever behind an orphan task.
            query = query.Where(task => task.PipelineStepRun == null
                || !preserved.Contains(task.PipelineStepRun.StageName));
        }

        if (db.Database.IsRelational())
        {
            // Two steps on purpose, and the second one repeats the status filter.
            //
            // Filtering on a navigation (the preserved-stage test) makes EF emit
            //   UPDATE "Tasks" AS t0 SET ... FROM (SELECT t."Id" ... WHERE t."Status" = ANY(...)) AS s
            //   WHERE t0."Id" = s."Id"
            // where the status lives in the FROM subquery instead of on the row being written. When a
            // concurrent transaction holds the row, PostgreSQL waits, then re-evaluates only the outer
            // qualification (`t0."Id" = s."Id"`, still true) under EvalPlanQual - the status is never
            // re-checked against the committed version. A task that turned Running between the scan and
            // the lock was therefore cancelled anyway, while TryStartTaskAsync had already answered
            // "started" - the exact divergence that failed the QA integration suite of run 1822.
            //
            // Selecting the ids first, then updating with the status predicate ON the target row, puts
            // the status back where EvalPlanQual re-evaluates it, so a row that left the eligible set
            // is skipped instead of overwritten.
            var eligibleIds = await query.Select(task => task.Id).ToListAsync(ct).ConfigureAwait(false);
            if (eligibleIds.Count == 0) return;
            await db.Tasks
                .Where(task => eligibleIds.Contains(task.Id) && statuses.Contains(task.Status))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(task => task.Status, TaskExecutionStatus.Cancelled)
                    .SetProperty(task => task.CompletedAt, now)
                    .SetProperty(task => task.EnvironmentVariables, TaskEnvProtection.EmptyEnv), ct)
                .ConfigureAwait(false);
            return;
        }

        foreach (var task in await query.ToListAsync(ct).ConfigureAwait(false))
        {
            task.Status = TaskExecutionStatus.Cancelled;
            task.CompletedAt = now;
            task.EnvironmentVariables = TaskEnvProtection.EmptyEnv;
        }
    }
}
