// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Analysis;

/// <summary>Shares a grade breakdown between the project Quality page and a pipeline run Quality
/// tab.</summary>
public partial class AnalysisGradeBreakdown
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The grade to break down; null renders nothing.</summary>
    [Parameter] public AnalysisGradeSummaryDto? Grade { get; set; }

    /// <summary>The run's letter came from the assurance seal because of blocking tests, so the
    /// code-and-tests domain limits it too, whatever its analysis letter.</summary>
    [Parameter] public bool SealBlockedByTests { get; set; }

    /// <summary>The project's latest average CRAP score, shown (advisory) under code quality.</summary>
    [Parameter] public double? CrapScore { get; set; }

    // Recette R-176: a domain with neither a measure nor a CRAP score is a compact line, not a card.
    private static bool HasDetails(DomainView domain) => domain.Measures.Any(IsShown) || domain.CrapScore.HasValue;

    // Every domain whose letter is the overall one sets the grade, several when they tie; nothing
    // limits an A, and an incomplete grade has no limiting domain yet.
    private bool IsLimiting(DomainView domain) =>
        (SealBlockedByTests && domain.CodeAndTests)
        || (Grade is { OverallGrade: > AnalysisGrade.A, Completeness: AnalysisGradeCompleteness.Complete }
            && domain.Grade == Grade.OverallGrade);

    /// <summary>
    /// Recette R1-60: the one measure the headline names as the cause of the grade: the first measure, in
    /// the domains' display order, whose letter is the overall one. Null for an A, an incomplete grade or a
    /// grade no measure explains (it then reads from the breakdown below).
    /// </summary>
    internal static AnalysisGradeMeasureDto? FirstLimitingMeasure(AnalysisGradeSummaryDto? grade)
    {
        if (grade is not { OverallGrade: > AnalysisGrade.A, Completeness: AnalysisGradeCompleteness.Complete }) return null;
        AnalysisGradeDomain[] order =
        [
            AnalysisGradeDomain.Security, AnalysisGradeDomain.Reliability, AnalysisGradeDomain.CodeQuality,
            AnalysisGradeDomain.Architecture, AnalysisGradeDomain.Performance
        ];
        return order
            .SelectMany(domain => grade.Domains.Where(candidate => candidate.Domain == domain))
            .SelectMany(domain => domain.Measures)
            .FirstOrDefault(measure => measure.Grade == grade.OverallGrade);
    }

    /// <summary>Recette R1-61: a count at zero says nothing; the tested lines and the most complex path
    /// stay even at zero, and a measure never observed stays to say so.</summary>
    internal static bool IsShown(AnalysisGradeMeasureDto measure) =>
        measure.Key is "grade.coverage.line" or "grade.complexity.maximum"
        || !measure.ObservedValue.HasValue
        || measure.ObservedValue.Value != 0
        || measure.Grade is > AnalysisGrade.A;

    // The distance to the next letter is in the unit of the value shown: a weighted finding score shows
    // the number of findings (recette R1-4), so its distance, in weighted points, would not match it.
    private static bool ShowDistance(AnalysisGradeMeasureDto measure) =>
        measure.DistanceToNextGrade.HasValue && measure.NextGrade.HasValue && !measure.WeightedFindingScore;

    private IReadOnlyList<DomainView> Domains
    {
        get
        {
            var source = Grade?.Domains ?? [];
            var result = new List<DomainView>();
            AddDomain(result, source, AnalysisGradeDomain.Security);

            var codeAndTests = source
                .Where(domain => domain.Domain is AnalysisGradeDomain.Reliability or AnalysisGradeDomain.CodeQuality)
                .ToArray();
            if (codeAndTests.Length > 0)
            {
                result.Add(new DomainView(
                    L["AnalysisCodeQualityAndTests"],
                    WorstGrade(codeAndTests),
                    codeAndTests.Any(domain => domain.Required),
                    codeAndTests.Any(domain => domain.Completeness == AnalysisGradeCompleteness.Incomplete)
                        ? AnalysisGradeCompleteness.Incomplete
                        : AnalysisGradeCompleteness.Complete,
                    codeAndTests.Where(domain => domain.EvaluatedAt.HasValue)
                        .Select(domain => domain.EvaluatedAt)
                        .Min(),
                    codeAndTests.SelectMany(domain => domain.Measures).ToArray(),
                    CrapScore,
                    CodeAndTests: true));
            }

            AddDomain(result, source, AnalysisGradeDomain.Architecture);
            // Show runtime performance only when a measurement has been observed.
            if (source.Any(domain => domain.Domain == AnalysisGradeDomain.Performance
                    && domain.Measures.Any(measure => measure.Observed)))
                AddDomain(result, source, AnalysisGradeDomain.Performance);
            return result;
        }
    }

    /// <summary>The label of a grade domain; reliability and code quality read as one.</summary>
    internal static string DomainLabel(IStringLocalizer<AppStrings> localizer, AnalysisGradeDomain? domain) => domain switch
    {
        AnalysisGradeDomain.Security => localizer["AnalysisApplicationSecurity"],
        AnalysisGradeDomain.Reliability or AnalysisGradeDomain.CodeQuality => localizer["AnalysisCodeQualityAndTests"],
        AnalysisGradeDomain.Architecture => localizer["AnalysisCodeArchitecture"],
        AnalysisGradeDomain.Performance => localizer["AnalysisRuntimePerformance"],
        _ => localizer["NotAvailable"]
    };

    private string MeasureLabel(string key) => MeasureLabel(L, key);

    internal static string MeasureLabel(IStringLocalizer<AppStrings> L, string key) => key switch
    {
        "grade.security.critical" => L["AnalysisCriticalVulnerabilities"],
        "grade.security.high" => L["AnalysisHighVulnerabilities"],
        "grade.security.medium" => L["AnalysisMediumVulnerabilities"],
        // Show open finding counts in the security and quality gates.
        "grade.security.findings" or "grade.quality.findings" => L["AnalysisOpenFindingCount"],
        "grade.coverage.line" => L["AnalysisTestedLines"],
        "grade.coverage.branch" => L["AnalysisTestedBranches"],
        "grade.complexity.maximum" => L["AnalysisMaximumComplexity"],
        "grade.duplication" => L["AnalysisDuplicatedCode"],
        "grade.architecture.cycles" => L["AnalysisDependencyCycles"],
        _ => key.StartsWith("grade.", StringComparison.OrdinalIgnoreCase)
            ? key["grade.".Length..].Replace('.', ' ')
            : key
    };

    internal static string MeasureText(AnalysisGradeMeasureDto measure)
    {
        if (!measure.ObservedValue.HasValue) return "-";
        // A finding score shows the number of open findings, the number the list shows.
        if (measure.FindingCounts is { } counts) return AnalysisFindingScore.Total(counts).ToString(CultureInfo.CurrentCulture);
        var value = measure.ObservedValue.Value.ToString("0.##");
        if (string.Equals(measure.Unit, "percent", StringComparison.OrdinalIgnoreCase)) return $"{value} %";
        return string.IsNullOrWhiteSpace(measure.Unit) ? value : $"{value} {measure.Unit}";
    }

    private void AddDomain(
        ICollection<DomainView> target,
        IReadOnlyList<AnalysisGradeDomainDto> source,
        AnalysisGradeDomain domain)
    {
        var item = source.FirstOrDefault(candidate => candidate.Domain == domain);
        if (item is null) return;
        target.Add(new DomainView(
            DomainLabel(L, domain), item.Grade, item.Required, item.Completeness, item.EvaluatedAt, item.Measures, null));
    }

    private static AnalysisGrade? WorstGrade(IEnumerable<AnalysisGradeDomainDto> domains) =>
        domains.Where(domain => domain.Grade.HasValue)
            .Select(domain => domain.Grade)
            .Max();

    private sealed record DomainView(
        string Label,
        AnalysisGrade? Grade,
        bool Required,
        AnalysisGradeCompleteness Completeness,
        DateTime? EvaluatedAt,
        IReadOnlyList<AnalysisGradeMeasureDto> Measures,
        double? CrapScore,
        bool CodeAndTests = false);
}
