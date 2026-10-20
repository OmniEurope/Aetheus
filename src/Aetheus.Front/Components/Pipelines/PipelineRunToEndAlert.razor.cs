// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The "run to end" banner of the run page: the mode, and every step that failed while the run went
/// on. Its own component so the run page stays within the file-size budget.
/// </summary>
public partial class PipelineRunToEndAlert
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineRunDto Run { get; set; } = default!;

    /// <summary>The failed (or timed-out) non-system steps, by name, in the order the run lists them.</summary>
    internal IReadOnlyList<string> FailedSteps =>
        Run.Steps
            .Where(step => !step.IsSystem
                && step.Status is TaskExecutionStatus.Failed or TaskExecutionStatus.Timeout)
            .Select(step => step.StepName)
            .ToList();
}
