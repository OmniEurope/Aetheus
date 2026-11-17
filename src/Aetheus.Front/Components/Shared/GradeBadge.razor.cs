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

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>True when the letter is an old, last known grade (R-12, R-20): an "Old" badge follows it.
    /// Ignored without a grade.</summary>
    [Parameter] public bool Old { get; set; }

    /// <summary>The grade to draw; null renders the neutral dash.</summary>
    [Parameter] public AnalysisGrade? Value { get; set; }

    /// <summary>Recette R-17: the larger letter of the grade block (project Quality, run Gate tab), where the
    /// badge stands alone on a line. Every other call site keeps the standard badge height, which a grid row
    /// is sized for.</summary>
    [Parameter] public bool Large { get; set; }

    private OmniControlSize BadgeSize => Large ? OmniControlSize.Large : OmniControlSize.Medium;

    /// <summary>Tooltip, usually the localized "overall grade" wording of the calling page.</summary>
    [Parameter] public string? Title { get; set; }

    /// <summary>Extra CSS classes from the call site.</summary>
    [Parameter] public string? Class { get; set; }
}
