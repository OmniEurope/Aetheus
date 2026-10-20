// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The candidate's sealed verdict, at the top of the run rather than in a step log.</summary>
public partial class PipelineRunAssuranceBanner
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public PipelineRunAssuranceVerdict.Verdict? Verdict { get; set; }

    /// <summary>Recette R1-79: the run on screen, which the log links keep.</summary>
    [Parameter] public int? ViewingRunId { get; set; }
}
