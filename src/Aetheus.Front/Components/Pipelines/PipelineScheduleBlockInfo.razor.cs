// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The small info of a block of the planner's calendar (recette R-44): the pipeline, its average duration
/// and its latest run with its state, duration, gate and grade.</summary>
public partial class PipelineScheduleBlockInfo
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    /// <summary>The scheduled pipeline the block belongs to.</summary>
    [Parameter, EditorRequired] public ScheduledTaskDto Task { get; set; } = default!;

    private string DurationText => SchedulePhrases.Duration(L, Task);

    private string SourceText => PipelineScheduleSourceText.Label(L, Task);

    private static OmniTone GateTone(AnalysisGateStatus status) => status switch
    {
        AnalysisGateStatus.Passed => OmniTone.Success,
        AnalysisGateStatus.Warning => OmniTone.Warning,
        AnalysisGateStatus.Blocked or AnalysisGateStatus.Error => OmniTone.Danger,
        _ => OmniTone.Neutral
    };
}
