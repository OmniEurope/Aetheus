// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.E2E;

/// <summary>One measured breach of a presentation rule. The <see cref="Key"/> identifies it across runs, so it holds
/// the page, the width, the rule and a stable element signature, never a measured value or a piece of data.</summary>
internal sealed record PageRuleViolation(string Page, int Width, string Rule, string Element, string Detail)
{
    public string Key => $"{Page} | {Width} | {Rule} | {Element}";
}

/// <summary>What a grid looked like at one width, to compare the row height of the two widths.</summary>
internal sealed record PageGridFact(string Grid, double RowHeight, int Rows);

/// <summary>Why a page that is on screen never stops changing: <see cref="Element"/> is stable and goes into the baseline key,
/// <see cref="Detail"/> holds the measured values for the report.</summary>
internal sealed record PageUnsettled(string Element, string Detail);

/// <summary>
/// A page of the inventory at one width: measured, redirected elsewhere, never settled (a loader that stays on screen: a
/// defect of the page, recorded as a violation), or <see cref="NotMeasuredReason"/> when the harness itself failed (never
/// baselined: the run fails).
/// </summary>
internal sealed record PageMeasurement(
    string Page,
    int Width,
    string FinalPath,
    IReadOnlyList<PageRuleViolation> Violations,
    IReadOnlyList<PageGridFact> Grids,
    IReadOnlyList<string> Intermittent,
    string? NotMeasuredReason,
    string? RedirectedTo,
    PageUnsettled? Unsettled,
    int Milliseconds)
{
    /// <summary>True when the page was on screen, settled, and measured.</summary>
    public bool Measured => NotMeasuredReason is null && RedirectedTo is null && Unsettled is null;
}

/// <summary>A route of the inventory with the address it resolves to in the seeded data, or why it has none.</summary>
internal sealed record PageTarget(PageRoute Route, string? Path, string? UnreachableReason)
{
    public string Template => Route.Template;
}

/// <summary>The outcome of a whole sweep: what was targeted, what was measured, and every violation found.</summary>
internal sealed record PageRuleSweep(
    IReadOnlyList<PageTarget> Targets,
    IReadOnlyList<PageMeasurement> Measurements,
    IReadOnlyList<PageRuleViolation> Violations,
    TimeSpan Elapsed);
