// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

internal sealed class PipelineCancellationRepository(
    AppDbContext db,
    TimeProvider timeProvider,
    IPipelineTaskLifecycle taskLifecycle)
{
    public async Task CancelActiveStepRunsAndTasksAsync(int runId, CancellationToken ct)
    {
        var activeStatuses = new[]
        {
            TaskExecutionStatus.Pending,
            TaskExecutionStatus.Assigned,
            TaskExecutionStatus.Running
        };
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        await taskLifecycle.CancelActiveTasksAsync(runId, ct).ConfigureAwait(false);

        var activeSteps = await db.PipelineStepRuns
            .Where(step => step.PipelineRunId == runId && activeStatuses.Contains(step.Status))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var step in activeSteps)
        {
            step.Status = TaskExecutionStatus.Cancelled;
            step.CompletedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (transaction is not null)
            await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task CancelPendingStepRunsAsync(int runId, CancellationToken ct)
    {
        var pendingSteps = await db.PipelineStepRuns
            .Where(step => step.PipelineRunId == runId
                           && (step.Status == TaskExecutionStatus.Pending
                               || step.Status == TaskExecutionStatus.Assigned))
            .ToListAsync(ct).ConfigureAwait(false);
        MarkCancelled(pendingSteps);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RequestPipelineRunCancellationAsync(int runId, PipelineRunCancellation cancellation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        var run = await db.PipelineRuns.FindAsync([runId], ct).ConfigureAwait(false);
        if (run is null) return;

        var variables = PipelineRunHelpers.DeserializeResolvedVariables(run.AdditionalVariablesJson);
        variables[PipelineRunService.CancellationRequestedVariable] = "true";
        // The first request names the author; a repeated click or the recovery that re-applies a
        // stalled cancellation does not rename it.
        variables.TryAdd(PipelineRunService.CancelledByVariable, cancellation.By);
        variables.TryAdd(PipelineRunService.CancellationReasonVariable, cancellation.Reason);
        run.AdditionalVariablesJson = JsonSerializer.Serialize(variables);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task CancelPendingStepRunsExceptStagesAsync(
        int runId,
        IReadOnlyCollection<string> preservedStages,
        CancellationToken ct)
    {
        var preserved = preservedStages.ToArray();
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;

        var run = await db.PipelineRuns
            .FirstOrDefaultAsync(item => item.Id == runId, ct)
            .ConfigureAwait(false);
        if (run is null) return;
        var variables = PipelineRunHelpers.DeserializeResolvedVariables(run.AdditionalVariablesJson);
        variables[PipelineRunService.CancellationRequestedVariable] = "true";
        run.AdditionalVariablesJson = JsonSerializer.Serialize(variables);

        await taskLifecycle.CancelPendingTasksExceptStagesAsync(runId, preserved, ct)
            .ConfigureAwait(false);

        var pendingSteps = await db.PipelineStepRuns
            .Where(step => step.PipelineRunId == runId
                           && (step.Status == TaskExecutionStatus.Pending
                               || step.Status == TaskExecutionStatus.Assigned
                               || (step.Status == TaskExecutionStatus.Running
                                   && db.Tasks.Any(task => task.PipelineStepRunId == step.Id
                                       && task.Status == TaskExecutionStatus.Cancelled)))
                           && !preserved.Contains(step.StageName))
            .ToListAsync(ct).ConfigureAwait(false);
        MarkCancelled(pendingSteps);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (transaction is not null)
            await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task CancelOrphanedRunningStepRunsExceptStagesAsync(
        int runId,
        IReadOnlyCollection<string> preservedStages,
        CancellationToken ct)
    {
        var preserved = preservedStages.ToArray();
        var orphanedSteps = await db.PipelineStepRuns
            .Where(step => step.PipelineRunId == runId
                           && step.Status == TaskExecutionStatus.Running
                           && !preserved.Contains(step.StageName)
                           && !db.Tasks.Any(task => task.PipelineStepRunId == step.Id
                               && (task.Status == TaskExecutionStatus.Pending
                                   || task.Status == TaskExecutionStatus.Assigned
                                   || task.Status == TaskExecutionStatus.Running)))
            .ToListAsync(ct).ConfigureAwait(false);
        MarkCancelled(orphanedSteps);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private void MarkCancelled(IEnumerable<PipelineStepRun> steps)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var step in steps)
        {
            step.Status = TaskExecutionStatus.Cancelled;
            step.CompletedAt = now;
        }
    }
}
