// SPDX-License-Identifier: EUPL-1.2

using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

/// <summary>A grade letter as a badge without icon, filled with the colour of its grade (recette R-59, R-107).
/// The fills are the <c>--aetheus-grade-*</c> tokens, applied by the <c>grade-badge--a</c> to
/// <c>grade-badge--f</c> classes of the stylesheet; the tone is the fallback meaning.</summary>
public partial class GradeBadge
{
    private static OmniTone VariantFor(AnalysisGrade grade) => grade switch
    {
        AnalysisGrade.A or AnalysisGrade.B => OmniTone.Success,
        AnalysisGrade.C => OmniTone.Warning,
        _ => OmniTone.Danger
    };

    /// <summary>The grade to draw; null renders the neutral dash.</summary>
    [Parameter] public AnalysisGrade? Value { get; set; }

    /// <summary>Tooltip, usually the localized "overall grade" wording of the calling page.</summary>
    [Parameter] public string? Title { get; set; }

    /// <summary>Extra CSS classes from the call site.</summary>
    [Parameter] public string? Class { get; set; }
}
