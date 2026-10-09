// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Analysis;

/// <summary>Recette R2-052: one grade's breakdown, shared by the project's Quality page and a run's
/// Quality tab. Moved out of <c>ProjectQualitySection</c> unchanged.</summary>
public partial class AnalysisGradeBreakdown
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The grade to break down; null renders nothing.</summary>
    [Parameter] public AnalysisGradeSummaryDto? Grade { get; set; }

    /// <summary>The project's latest average CRAP score, shown (advisory) under code quality.</summary>
    [Parameter] public double? CrapScore { get; set; }

    /// <summary>Name the domain that sets the grade above the domains (a run's tab has no headline that
    /// already says it).</summary>
    [Parameter] public bool ShowLimitingDomain { get; set; }

    // Recette R-176: a domain with neither a measure nor a CRAP score is a compact line, not a card.
    private static bool HasDetails(DomainView domain) => domain.Measures.Count > 0 || domain.CrapScore.HasValue;

    private string DomainState(DomainView domain) =>
        $"{(domain.Required ? L["AnalysisRequired"] : L["AnalysisAdvisory"])}"
        + $" · {(domain.Completeness == AnalysisGradeCompleteness.Complete ? L["Complete"] : L["Incomplete"])}"
        + $" · {L["AnalysisFreshness"]}: {domain.EvaluatedAt?.ToString("g") ?? L["NotAvailable"]}";

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
                    CrapScore));
            }

            AddDomain(result, source, AnalysisGradeDomain.Architecture);
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

    private string MeasureLabel(string key) => key switch
    {
        "grade.security.critical" => L["AnalysisCriticalVulnerabilities"],
        "grade.security.high" => L["AnalysisHighVulnerabilities"],
        "grade.security.medium" => L["AnalysisMediumVulnerabilities"],
        "grade.security.findings" => L["AnalysisOpenFindingsScore"],
        "grade.coverage.line" => L["AnalysisTestedLines"],
        "grade.coverage.branch" => L["AnalysisTestedBranches"],
        "grade.complexity.maximum" => L["AnalysisMaximumComplexity"],
        "grade.duplication" => L["AnalysisDuplicatedCode"],
        "grade.architecture.cycles" => L["AnalysisDependencyCycles"],
        _ => key.StartsWith("grade.", StringComparison.OrdinalIgnoreCase)
            ? key["grade.".Length..].Replace('.', ' ')
            : key
    };

    private static string MeasureText(AnalysisGradeMeasureDto measure)
    {
        if (!measure.ObservedValue.HasValue) return "-";
        var value = measure.ObservedValue.Value.ToString("0.##");
        if (string.Equals(measure.Unit, "percent", StringComparison.OrdinalIgnoreCase)) return $"{value} %";
        return string.IsNullOrWhiteSpace(measure.Unit) ? value : $"{value} {measure.Unit}";
    }

    // "Critical 0 · High 1 (×10) · Medium 2 (×3) · Low 0 (×1) · Info 1 (×0.5)": the score is the sum of
    // the weighted counts, and any critical finding gives F whatever the score.
    private string FindingCountsText(AnalysisGradeFindingCountsDto counts) => string.Join(" · ",
        $"{L.Localize(AnalysisSeverity.Critical)} {counts.Critical}",
        WeightedCount(AnalysisSeverity.High, counts.High, AnalysisFindingScore.HighWeight),
        WeightedCount(AnalysisSeverity.Medium, counts.Medium, AnalysisFindingScore.MediumWeight),
        WeightedCount(AnalysisSeverity.Low, counts.Low, AnalysisFindingScore.LowWeight),
        WeightedCount(AnalysisSeverity.Info, counts.Info, AnalysisFindingScore.InfoWeight));

    private string WeightedCount(AnalysisSeverity severity, int count, double weight) =>
        $"{L.Localize(severity)} {count} (×{weight:0.##})";

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
        double? CrapScore);
}
