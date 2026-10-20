// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Completes <c>type: trigger</c> steps that are waiting on a child run. When any pipeline run finishes,
/// this finds the parent step(s) that launched it (linked via <c>PipelineStepRun.TriggeredRunId</c>) and
/// still <see cref="TaskExecutionStatus.Running"/>, mirrors the child's terminal status onto the step
/// (Success ⇒ Success; anything else ⇒ Failed, respecting the step's continue_on_error downstream), then
/// re-enters <c>AdvanceStageAsync</c> so the parent run's stage can progress. This is the wait half of the
/// orchestration primitive - the dispatch half is <c>PipelineRunService.CreateTriggerStepAsync</c>.
/// </summary>
public sealed class PipelineRunCompletedTriggerHandler(
    IPipelineRepository repo,
    IPipelineRunService runService,
    ILogger<PipelineRunCompletedTriggerHandler> logger)
    : IDomainEventHandler<PipelineRunCompletedEvent>
{
    public async Task HandleAsync(PipelineRunCompletedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var waitingSteps = await repo.FindStepRunsByTriggeredRunIdAsync(domainEvent.PipelineRunId, ct).ConfigureAwait(false);
        if (!waitingSteps.Any(step => step.Status == TaskExecutionStatus.Running))
            return;
        // A Rejected child (recette R1-80) hands up its outputs too: they say what it found.
        var childOutputs = domainEvent.Status is PipelineStatus.Success or PipelineStatus.Rejected
            ? await CollectChildOutputsAsync(domainEvent.PipelineRunId, ct).ConfigureAwait(false)
            : [];

        foreach (var step in waitingSteps)
        {
            if (step.Status != TaskExecutionStatus.Running) continue; // already resolved / not actually waiting

            if (!await runService.ResolveCompletedTriggerStepAsync(
                    step, domainEvent.Status, childOutputs, ct).ConfigureAwait(false))
            {
                logger.LogDebug(
                    "Trigger step {StepId} was already resolved before child run {ChildRun} completed",
                    step.Id, domainEvent.PipelineRunId);
            }
        }
    }

    private async Task<Dictionary<string, string>> CollectChildOutputsAsync(
        int childRunId,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var outputs = await repo.GetSuccessfulStepOutputsAsync(childRunId, ct).ConfigureAwait(false) ?? [];
        foreach (var step in outputs)
        {
            foreach (var (name, value) in PipelineRunHelpers.DeserializeResolvedVariables(step.OutputVariablesJson))
                result[name] = value;
        }
        return result;
    }
}
