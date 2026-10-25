// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineRunGateOverview
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Parameter, EditorRequired] public AnalysisRunGateDto Gate { get; set; } = default!;

    /// <summary>The address of the run's Gate tab; the tile is that link. Null leaves a plain tile.</summary>
    [Parameter] public string? Href { get; set; }

    /// <summary>The letter the run is listed with, shown in place of the analysis letter.</summary>
    [Parameter] public AnalysisGrade? RunGrade { get; set; }
}
