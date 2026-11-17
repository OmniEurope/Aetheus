// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Tasks;

public interface IPipelineTaskLifecycle
{
    Task CancelActiveTasksAsync(int pipelineRunId, CancellationToken ct = default);
    /// <summary>Cancels the Pending, Assigned and Running tasks of the run outside
    /// <paramref name="preservedStages"/>; a Running task is stopped by its agent once it sees the
    /// Cancelled status.</summary>
    Task CancelActiveTasksExceptStagesAsync(
        int pipelineRunId,
        IReadOnlyCollection<string> preservedStages,
        CancellationToken ct = default);

    /// <summary>Cancels the tasks of <paramref name="stepRunIds"/> that an agent could still pick up or
    /// keep running, and empties their environment.</summary>
    Task CancelActiveTasksOfStepRunsAsync(
        int pipelineRunId,
        IReadOnlyCollection<int> stepRunIds,
        CancellationToken ct = default);
}
