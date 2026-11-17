// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Tasks.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Tasks;

internal sealed class TaskCompletionFinalizer(
    ITaskRepository repo,
    ILogService logService,
    IAuditService audit,
    TimeProvider timeProvider,
    IArtifactService artifactService,
    ILogger logger,
    IDomainEventDispatcher domainEvents)
{
    /// <summary>
    /// What the task reported as its output: the result text, else the output-variable marker lines of its
    /// log. Read BEFORE the completion is persisted (R-18): the step must become Success together with the
    /// variables it published, because a sibling step finishing in the same instant advances the run on
    /// every step it sees as Success.
    /// </summary>
    internal async Task<string?> ReadReportedOutputAsync(TaskResultDto result, int taskId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(result.Output))
            return result.Output;
        var markerLines = await logService.GetTaskOutputVariableLinesAsync(taskId, ct).ConfigureAwait(false);
        return markerLines.Count > 0 ? string.Join("\n", markerLines) : null;
    }

    /// <summary>The JSON a step stores as its published variables, or null when it published none.</summary>
    internal static string? SerializeOutputVariables(string? reportedOutput) =>
        string.IsNullOrEmpty(reportedOutput) ? null : SerializeOutputVariables(TaskOutputVariableParser.Parse(reportedOutput));

    private static string? SerializeOutputVariables(IReadOnlyDictionary<string, string> outputVars) =>
        outputVars.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(outputVars) : null;

    internal async Task<PipelineStepRun?> UpdateStepRunAsync(
        ServerTask task,
        TaskResultDto result,
        string? logOutput,
        CancellationToken ct)
    {
        if (!task.PipelineStepRunId.HasValue)
            return null;
        var stepRun = await repo.FindPipelineStepRunAsync(task.PipelineStepRunId.Value, ct).ConfigureAwait(false);
        if (stepRun is null)
            return null;
        stepRun.Status = result.Status;
        stepRun.ExitCode = result.ExitCode;
        // The fenced completion already stamped the step with the instant it stamped the task (the same
        // UPDATE batch, read back above), so a second reading of the clock would only cost an UPDATE for
        // a few microseconds. Only a step nobody stamped (the N-1 unfenced protocol) gets the time now.
        stepRun.CompletedAt ??= timeProvider.GetUtcNow().UtcDateTime;

        if (!string.IsNullOrEmpty(logOutput))
        {
            var outputVars = TaskOutputVariableParser.Parse(logOutput);
            // The fenced completion persisted these together with the status; this keeps the unfenced
            // protocol and the in-memory store whole.
            if (outputVars.Count > 0)
                stepRun.OutputVariablesJson = SerializeOutputVariables(outputVars);

            if (task.PipelineRunId is { } pipelineRunId)
            {
                var metrics = TaskPipelineMetricParser.Parse(
                    logOutput,
                    pipelineRunId,
                    stepRun.StageName,
                    stepRun.StepName,
                    stepRun.CompletedAt.Value);
                // Strict: metrics quietly dropped would make a run page under-report while every
                // step still reported success.
                if (metrics.Count > 0)
                    await domainEvents
                        .DispatchStrictAsync(new PipelineStepTaskMetricsCollectedEvent(pipelineRunId, metrics), ct)
                        .ConfigureAwait(false);

                // Recette R1-43: the expand/contract verdict of a blue-green migrate step, kept
                // against the run so it outlives the step log.
                var gateVerdicts = TaskMigrationGateParser.Parse(
                    logOutput,
                    pipelineRunId,
                    stepRun.StageName,
                    stepRun.StepName,
                    stepRun.CompletedAt.Value);
                if (gateVerdicts.Count > 0)
                    await domainEvents
                        .DispatchStrictAsync(new PipelineStepMigrationGateReportedEvent(pipelineRunId, gateVerdicts), ct)
                        .ConfigureAwait(false);

                // Recette R-75: a step that succeeded on purpose can still say that what it was meant to
                // check did not happen; strict, for the same reason as the metrics.
                var runWarnings = TaskRunWarningParser.Parse(outputVars);
                if (runWarnings.Count > 0)
                    await domainEvents
                        .DispatchStrictAsync(new PipelineStepRunWarningsReportedEvent(pipelineRunId, runWarnings), ct)
                        .ConfigureAwait(false);
            }
        }
        return stepRun;
    }

    internal async Task AuditDeferredCleanupAsync(ServerTask task)
    {
        if (!task.IsDeferredCleanup)
            return;
        await audit.LogAsync(
            task.Status == TaskExecutionStatus.Success
                ? "ReconciledDeferredPipelineCleanup"
                : "FailedDeferredPipelineCleanup",
            "PipelineRun",
            task.PipelineRunId ?? 0,
            $"Task {task.Id}; runner {task.ServerId}; status {task.Status}",
            CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task CloseDeploymentAsync(
        ServerTask task,
        PipelineStepRun? stepRun,
        DeployClosure closure,
        CancellationToken ct)
    {
        try
        {
            var marked = await artifactService.MarkDeployedAsync(
                closure.ArtifactId, closure.Cohort, closure.ReleaseId, ct).ConfigureAwait(false);
            if (!marked)
                throw new InvalidOperationException($"Deployed artifact {closure.ArtifactId} no longer exists.");
            if (closure.RollbackId is { } rollbackId)
                await domainEvents.DispatchAsync(
                    new RollbackDeploymentSucceededEvent(rollbackId), ct).ConfigureAwait(false);

            // Consequences of the deployment, not conditions for it: DispatchAsync keeps handler
            // failures out of the closure's failure path, so a post-deployment observer can never
            // turn a live release into a failed step.
            if (closure.ReleaseId is { } deployedReleaseId)
                await domainEvents.DispatchAsync(
                    new ReleaseDeployedEvent(
                        deployedReleaseId,
                        task.PipelineRunId,
                        stepRun?.StageName,
                        closure.RollbackId is not null),
                    ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Deploy closure failed for task {TaskId}, artifact {ArtifactId}, release {ReleaseId}",
                task.Id, closure.ArtifactId, closure.ReleaseId);
            task.Status = TaskExecutionStatus.Failed;
            task.ExitCode = -1;
            if (stepRun is not null)
            {
                stepRun.Status = TaskExecutionStatus.Failed;
                stepRun.ExitCode = -1;
            }
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}

internal readonly record struct DeployClosure(
    int ArtifactId,
    string Cohort,
    int? RollbackId,
    int? ReleaseId);
