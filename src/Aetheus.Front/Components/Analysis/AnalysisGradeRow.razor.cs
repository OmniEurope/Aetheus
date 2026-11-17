// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Analysis;

/// <summary>A line of the grade block (the Gate grade, a domain or a measure): its label with the help mark
/// that holds its whole detail, its value and its grade letter. No accordion (recette R-16).</summary>
public partial class AnalysisGradeRow
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public string Label { get; set; } = string.Empty;

    /// <summary>Everything the line explains, one line of text per fact (criterion, thresholds, measured
    /// value, evaluation date, cause), shown in the tooltip of the help mark.</summary>
    [Parameter, EditorRequired] public string Help { get; set; } = string.Empty;

    [Parameter] public string? Value { get; set; }

    [Parameter] public AnalysisGrade? Grade { get; set; }

    /// <summary>The grade is an old, last known one (R-12, R-20): its badge says so.</summary>
    [Parameter] public bool GradeOld { get; set; }

    /// <summary>False for a line that has no grade by design (the CRAP average): the value stands alone, no "-" badge.</summary>
    [Parameter] public bool ShowGrade { get; set; } = true;

    /// <summary>Bold: the line sets the overall grade.</summary>
    [Parameter] public bool Limiting { get; set; }

    /// <summary><c>gate</c>, <c>domain</c> or <c>measure</c>: only the indentation and weight differ.</summary>
    [Parameter] public string Kind { get; set; } = "measure";

    [Parameter] public string? TestId { get; set; }

    /// <summary>Muted second lines kept visible under the line (distance to the next grade).</summary>
    [Parameter] public RenderFragment? Hints { get; set; }
}
