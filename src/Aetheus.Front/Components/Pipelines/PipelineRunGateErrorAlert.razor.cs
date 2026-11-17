// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The reasons of an Error Gate verdict, listed in an alert on the Gate tab.</summary>
public partial class PipelineRunGateErrorAlert
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public AnalysisRunGateDto Gate { get; set; } = default!;

    private IReadOnlyList<string> Lines => PipelineRunGateErrorCauses.Lines(Gate, L);
}
