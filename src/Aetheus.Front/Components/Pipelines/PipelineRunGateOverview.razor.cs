// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineRunGateOverview
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Parameter, EditorRequired] public AnalysisRunGateDto Gate { get; set; } = default!;

    /// <summary>The address of the run's Gate tab; the tile is that link. Null leaves a plain tile.</summary>
    [Parameter] public string? Href { get; set; }

    /// <summary>R-36: the tests the seal held the candidate back for; they are findings of the gate and are counted
    /// with them, as on the Gate tab.</summary>
    [Parameter] public int BlockingTestCount { get; set; }

    /// <summary>The letter the run is listed with, shown in place of the analysis letter.</summary>
    [Parameter] public AnalysisGrade? RunGrade { get; set; }
}
