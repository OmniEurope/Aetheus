// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Analysis;

/// <summary>A line of the grade block (the Gate grade, a domain or a measure): its criterion in a
/// tooltip, its detail one click away.</summary>
public partial class AnalysisGradeRow
{
    [Parameter, EditorRequired] public string Label { get; set; } = string.Empty;

    /// <summary>The criterion of the line, in words.</summary>
    [Parameter, EditorRequired] public string Tooltip { get; set; } = string.Empty;

    [Parameter] public string? Value { get; set; }

    [Parameter] public AnalysisGrade? Grade { get; set; }

    /// <summary>Bold: the line sets the overall grade.</summary>
    [Parameter] public bool Limiting { get; set; }

    /// <summary><c>gate</c>, <c>domain</c> or <c>measure</c>: only the indentation and weight differ.</summary>
    [Parameter] public string Kind { get; set; } = "measure";

    [Parameter] public string? TestId { get; set; }

    /// <summary>Muted second lines kept visible under the line (distance to the next grade).</summary>
    [Parameter] public RenderFragment? Hints { get; set; }

    /// <summary>What opens when the line is clicked.</summary>
    [Parameter] public RenderFragment? Details { get; set; }
}
