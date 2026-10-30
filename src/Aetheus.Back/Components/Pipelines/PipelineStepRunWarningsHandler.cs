// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Puts the warning a step published in its <c>RUN_WARNING</c> output variable on its run, and on every run that
/// triggered it (recette R-75): the QA pipeline's run says that nothing was validated, and so does the candidate run
/// whose QA stage triggered it, because the candidate is the run that goes on to publish a release. Tasks reads the
/// variable because it owns the log; this side owns the runs and therefore the write.
/// </summary>
internal sealed class PipelineStepRunWarningsHandler(IPipelineRepository repo)
    : IDomainEventHandler<PipelineStepRunWarningsReportedEvent>
{
    /// <summary>The orchestration chain is refused past ten triggers (PipelineTriggerStepCoordinator).</summary>
    private const int MaxAncestors = 10;

    public async Task HandleAsync(PipelineStepRunWarningsReportedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.Warnings.Count == 0) return;

        await repo.AppendRunWarningsAsync(domainEvent.PipelineRunId, [.. domainEvent.Warnings], ct).ConfigureAwait(false);

        var seen = new HashSet<int> { domainEvent.PipelineRunId };
        var current = new[] { domainEvent.PipelineRunId };
        for (var depth = 0; depth < MaxAncestors && current.Length > 0; depth++)
        {
            var parents = new List<int>();
            foreach (var childRunId in current)
            {
                var triggeringSteps = await repo.FindStepRunsByTriggeredRunIdAsync(childRunId, ct).ConfigureAwait(false);
                foreach (var step in triggeringSteps.Where(step => seen.Add(step.PipelineRunId)))
                {
                    await repo.AppendRunWarningsAsync(
                        step.PipelineRunId,
                        [.. domainEvent.Warnings.Select(warning => ForParent(step.StageName, childRunId, warning))],
                        ct).ConfigureAwait(false);
                    parents.Add(step.PipelineRunId);
                }
            }
            current = [.. parents];
        }
    }

    /// <summary>The warning of a child as its parent stores it. A warning the child stored as a problem (a step that named
    /// it, recette R-23) is carried as the inner problem, so the parent says it in the user's language too and its
    /// English sentence holds the child's English, never the stored form.</summary>
    private static string ForParent(string stageName, int childRunId, string warning)
    {
        var inner = StoredProblem.TryRead(warning, out var content) ? content : null;
        var detail = inner?.English ?? warning;
        return PipelineProblem.Of(
                PipelineProblemCodes.ChildRunWarning,
                $"Stage '{stageName}' (run #{childRunId}): {detail}",
                ("stage", stageName), ("run", childRunId), ("detail", detail))
            .ToStored(inner is null ? null : [inner.Problem]);
    }
}
