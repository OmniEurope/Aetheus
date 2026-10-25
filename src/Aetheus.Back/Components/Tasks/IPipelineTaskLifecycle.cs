// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Tasks;

public interface IPipelineTaskLifecycle
{
    Task CancelActiveTasksAsync(int pipelineRunId, CancellationToken ct = default);
    Task CancelPendingTasksExceptStagesAsync(
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
