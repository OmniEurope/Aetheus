// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Tasks;

/// <summary>
/// Fails a task that no agent will ever be able to claim, with a stated reason, and settles the
/// pipeline step that owns it so the run fails instead of hanging. The claim filter silently skips
/// work an agent cannot execute; this is what turns that silence into a result.
/// </summary>
public interface IUnexecutableTaskRefuser
{
    /// <summary><c>true</c> when this call failed the task; <c>false</c> when it was no longer Pending
    /// (claimed, cancelled or timed out first) and was left untouched.</summary>
    Task<bool> RefuseAsync(int taskId, string failureCode, string reason, CancellationToken ct = default);
}

internal sealed class UnexecutableTaskRefuser(
    ITaskRepository repo,
    IHubContext<PipelineHub> pipelineHub,
    IHubContext<ServerHub> serverHub,
    IDomainEventDispatcher domainEvents,
    TimeProvider timeProvider,
    ILogger<UnexecutableTaskRefuser> logger) : IUnexecutableTaskRefuser
{
    public async Task<bool> RefuseAsync(int taskId, string failureCode, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var failedAt = timeProvider.GetUtcNow().UtcDateTime;
        if (!await repo.TryFailPendingTaskAsync(taskId, failureCode, reason, failedAt, ct).ConfigureAwait(false))
            return false;

        var task = await repo.FindTaskAsync(taskId, ct).ConfigureAwait(false);
        if (task is null)
            return true;

        // The log panel of a task that never ran would otherwise read "no logs available".
        PipelineTaskFailureClassifier.AddIncidentLog(task, failedAt);

        PipelineStepRun? stepRun = null;
        var stepTransitioned = false;
        if (task.PipelineStepRunId is { } stepRunId)
        {
            stepRun = await repo.FindPipelineStepRunAsync(stepRunId, ct).ConfigureAwait(false);
            if (stepRun is { CompletedAt: null })
            {
                stepRun.Status = TaskExecutionStatus.Failed;
                stepRun.ExitCode = -1;
                stepRun.FailureCode = failureCode;
                stepRun.FailureReason = reason;
                stepRun.CompletedAt = failedAt;
                stepTransitioned = true;
            }
        }
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogWarning(
            "Task #{TaskId} ({TaskName}) of server #{ServerId} was failed without running: {Reason}",
            task.Id, task.Name, task.ServerId, reason);

        // The authoritative state machine first, the best-effort notifications after: a SignalR
        // outage must not strand a failed step between stages.
        if (task.PipelineRunId is { } runId
            && (stepTransitioned || (stepRun is null && task.Operation == OperationKind.PipelineCollectArtifacts)))
        {
            await domainEvents.DispatchStrictAsync(
                new PipelineStepTaskCompletedEvent(
                    runId,
                    stepRun?.StageName,
                    TaskExecutionStatus.Failed,
                    task.Operation == OperationKind.PipelineCollectArtifacts),
                ct).ConfigureAwait(false);
        }

        await BroadcastAsync(task, stepTransitioned, ct).ConfigureAwait(false);
        return true;
    }

    private async Task BroadcastAsync(ServerTask task, bool stepTransitioned, CancellationToken ct)
    {
        var notification = new TaskCompletedNotification
        {
            TaskId = task.Id,
            ServerId = task.ServerId,
            TaskName = task.Name,
            Status = TaskExecutionStatus.Failed,
            ExitCode = task.ExitCode,
            Output = null,
            Operation = task.Operation
        };
        try
        {
            await Task.WhenAll(
                serverHub.Clients.Group(HubGroups.Server(task.ServerId)).SendAsync("TaskCompleted", notification, ct),
                serverHub.Clients.Group(HubGroups.AllServers).SendAsync("TaskCompleted", notification, ct)
            ).ConfigureAwait(false);

            if (task.PipelineRunId.HasValue && stepTransitioned)
                await pipelineHub.Clients.Group($"pipeline-run-{task.PipelineRunId}")
                    .SendAsync("StepCompleted", task.PipelineStepRunId, TaskExecutionStatus.Failed, ct)
                    .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failure notification failed for task {TaskId}", task.Id);
        }
    }
}
