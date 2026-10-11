// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Analysis;

/// <summary>Recette R2-052: one grade's breakdown, shared by the project's Quality page and a run's
/// Quality tab. Moved out of <c>ProjectQualitySection</c> unchanged.</summary>
public partial class AnalysisGradeBreakdown
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The grade to break down; null renders nothing.</summary>
    [Parameter] public AnalysisGradeSummaryDto? Grade { get; set; }

    /// <summary>Recette R2-139: the run's letter came from the assurance seal because of blocking tests,
    /// so the code-and-tests domain limits it too, whatever its analysis letter.</summary>
    [Parameter] public bool SealBlockedByTests { get; set; }

    /// <summary>The project's latest average CRAP score, shown (advisory) under code quality.</summary>
    [Parameter] public double? CrapScore { get; set; }

    // Recette R-176: a domain with neither a measure nor a CRAP score is a compact line, not a card.
    private static bool HasDetails(DomainView domain) => domain.Measures.Count > 0 || domain.CrapScore.HasValue;

    // Recette R2-139: every domain whose letter is the overall one sets the grade, several when they tie;
    // nothing limits an A, and an incomplete grade has no limiting domain yet.
    private bool IsLimiting(DomainView domain) =>
        (SealBlockedByTests && domain.CodeAndTests)
        || (Grade is { OverallGrade: > AnalysisGrade.A, Completeness: AnalysisGradeCompleteness.Complete }
            && domain.Grade == Grade.OverallGrade);

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
            // Recette R2-142: runtime performance shows only once a measure of it was really observed;
            // no project measures it yet, and an empty "Incomplete" line only says that.
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

    private string MeasureLabel(string key) => key switch
    {
        "grade.security.critical" => L["AnalysisCriticalVulnerabilities"],
        "grade.security.high" => L["AnalysisHighVulnerabilities"],
        "grade.security.medium" => L["AnalysisMediumVulnerabilities"],
        // Recettes R2-140 and R1-4: the open findings, as a number, in the security and the quality gates.
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

    private static string MeasureText(AnalysisGradeMeasureDto measure)
    {
        if (!measure.ObservedValue.HasValue) return "-";
        // Recette R1-4: a finding score shows the number of open findings, the number the list shows.
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
