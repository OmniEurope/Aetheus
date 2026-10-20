// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;

namespace Aetheus.Front.Components.Analysis;

public partial class AnalysisFindingColumns
{
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The filters the columns open with (open findings, or what a shortcut asked for). Null
    /// opens them without any.</summary>
    [Parameter] public FindingColumnDefaults? Defaults { get; set; }

    /// <summary>True when the grid holds every row itself: the location can then be sorted and
    /// filtered, which a list read from the server page by page cannot do.</summary>
    [Parameter] public bool LocalData { get; set; }

    /// <summary>The pipeline run whose findings are listed. A finding opened from that list shows
    /// the file and the commit this run analysed, not those of its latest occurrence.</summary>
    [Parameter] public int? RunId { get; set; }

    /// <summary>The status column. A list of findings all in one status leaves it out.</summary>
    [Parameter] public bool ShowStatus { get; set; } = true;

    /// <summary>Who owns the finding and when it was last seen: known to the project's list, not to the
    /// findings of one run.</summary>
    [Parameter] public bool ShowOwnership { get; set; } = true;

    /// <summary>The rows of decided findings carry "Revert the decision" (the caller shows decided
    /// findings and may decide on them).</summary>
    [Parameter] public bool CanRevertDecisions { get; set; }

    /// <summary>Raised by "Revert the decision" on a row; the caller confirms and reverts.</summary>
    [Parameter] public EventCallback<AnalysisFindingDto> RevertDecision { get; set; }

    // Built once so the columns see the same delegates on every render.
    private Func<string, string>? _severityText;
    private Func<string, string>? _statusText;
    private Func<string, string>? _categoryText;
    private Func<string, string>? _yesNoText;
    private Func<string, string> SeverityText => _severityText ??= GridFilterText.ForEnum<AnalysisSeverity>(L);
    private Func<string, string> StatusText => _statusText ??= GridFilterText.ForEnum<AnalysisFindingStatus>(L);
    private Func<string, string> CategoryText => _categoryText ??= GridFilterText.ForEnum<AnalysisCategory>(L);
    private Func<string, string> YesNoText => _yesNoText ??= GridFilterText.YesNo(L);

    // What the export bar writes for the columns whose cell is a badge or a composed text, so the
    // file reads as the screen (translated names, path:line, yes or no), not enum members.
    private Func<AnalysisFindingDto, object?>? _severityExport;
    private Func<AnalysisFindingDto, object?>? _categoryExport;
    private Func<AnalysisFindingDto, object?>? _statusExport;
    private Func<AnalysisFindingDto, object?>? _isNewExport;
    private Func<AnalysisFindingDto, object?> SeverityExport => _severityExport ??= finding => L.Localize(finding.Severity);
    private Func<AnalysisFindingDto, object?> CategoryExport => _categoryExport ??= finding => L.Localize(finding.Category);
    private Func<AnalysisFindingDto, object?> StatusExport => _statusExport ??= finding => L.Localize(finding.Status);
    private Func<AnalysisFindingDto, object?> IsNewExport => _isNewExport ??= finding => L[finding.LatestOccurrence?.IsNew == true ? "Yes" : "No"].Value;

    private static readonly Func<AnalysisFindingDto, object?> LocationExport =
        finding => finding.LatestOccurrence is { } occurrence ? LocationText(occurrence) : string.Empty;

    /// <summary>The page of one finding, as seen by a run when one is given.</summary>
    public static string FindingHref(int findingId, int? runId = null) => runId is { } run
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"/analysis/findings/{findingId}?run={run}")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"/analysis/findings/{findingId}");

    private void Open(AnalysisFindingDto finding) => Navigation.NavigateTo(FindingHref(finding.Id, RunId));

    /// <summary>The title and, under it, the message, read on hover of the one-line
    /// title.</summary>
    internal static string TitleTooltip(AnalysisFindingDto finding) =>
        string.IsNullOrWhiteSpace(finding.Message) || finding.Message == finding.Title
            ? finding.Title
            : $"{finding.Title}\n{finding.Message}";

    /// <summary><c>path:line</c>, the form an editor and a terminal both read.</summary>
    internal static string LocationText(AnalysisFindingOccurrenceDto occurrence) =>
        occurrence.StartLine is { } line
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{occurrence.FilePath}:{line}")
            : occurrence.FilePath ?? string.Empty;

    /// <summary>
    /// A finding of one pipeline run as the row every findings list shows: what the run saw of it (file,
    /// line, "new") is its latest occurrence.
    /// </summary>
    public static AnalysisFindingDto ToRow(AnalysisRunGateFindingDto finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return new AnalysisFindingDto
        {
            Id = finding.FindingId,
            RuleId = finding.RuleId,
            Title = finding.Title,
            Message = finding.Message,
            Category = finding.Category,
            Severity = finding.Severity,
            Status = finding.Status,
            LatestOccurrence = new AnalysisFindingOccurrenceDto
            {
                RuleId = finding.RuleId,
                FilePath = finding.FilePath,
                StartLine = finding.StartLine,
                Message = finding.Message,
                IsNew = finding.IsNew
            }
        };
    }
}
