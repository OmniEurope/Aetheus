// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The stop reason of one step (<see cref="PipelineStepStopReason"/>); nothing for a step that
/// did not fail, time out or get cancelled.</summary>
public partial class PipelineStepStopAlert
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineStepRunDto Step { get; set; } = default!;

    /// <summary>R-21: the last error line of the step's log (<see cref="PipelineStepStopReason.LastErrorLine"/>), shown under the headline.</summary>
    [Parameter] public string? LastErrorLine { get; set; }

    private PipelineStepStopReason.View? View => PipelineStepStopReason.For(Step, L);
}
