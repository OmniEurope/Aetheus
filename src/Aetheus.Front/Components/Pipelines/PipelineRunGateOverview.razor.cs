// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineRunGateOverview
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Parameter, EditorRequired] public AnalysisRunGateDto Gate { get; set; } = default!;
    [Parameter] public EventCallback GateRequested { get; set; }

    /// <summary>Recette R2-129: the letter the run is listed with, shown in place of the analysis letter.</summary>
    [Parameter] public AnalysisGrade? RunGrade { get; set; }
}
