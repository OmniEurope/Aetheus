// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The stop reason of one step (<see cref="PipelineStepStopReason"/>); nothing for a step that
/// did not fail, time out or get cancelled.</summary>
public partial class PipelineStepStopAlert
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineStepRunDto Step { get; set; } = default!;

    private PipelineStepStopReason.View? View => PipelineStepStopReason.For(Step, L);
}
