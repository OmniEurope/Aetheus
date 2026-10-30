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

    /// <summary>Recette R1-38: draw the run's single grade block (the Gate grade as its first row) instead of
    /// the two-column domain cards.</summary>
    [Parameter] public bool GateRow { get; set; }

    /// <summary>The grade the run shows (the sealed letter when it is not the analysis one).</summary>
    [Parameter] public AnalysisGrade? GateGrade { get; set; }

    // What sets the Gate grade, in the value column of its row: the blocking tests when the seal is
    // the cause, else the first measure carrying the overall letter.
    private string? GateCause => SealBlockedByTests
        ? L["GateFirstCauseBlockingTests"].Value
        : FirstLimitingMeasure(Grade) is { } cause ? $"{MeasureLabel(cause.Key)} : {MeasureValue(cause)}" : null;

    // The block reads the code and tests first, then the other domains in their usual order.
    private IReadOnlyList<DomainView> BlockDomains => [.. Domains.OrderBy(domain => domain.CodeAndTests ? 0 : 1)];

    /// <summary>
    /// Recettes R-73 and R-74: what a domain says in place of a letter. A domain always has a grade (no finding is an A),
    /// so the only domain without a letter is one the grade requires and nobody measured: it reads "No measure", never
    /// "Incomplete". Null for a domain with a grade, and for one the grade does not require.
    /// </summary>
    private string? NoGradeText(DomainView domain) =>
        domain.Grade is null && domain.Required ? L["AnalysisGradeNoMeasure"].Value : null;

    // Recette R-176: a domain with neither a measure nor a CRAP score is a compact line, not a card.
    private static bool HasDetails(DomainView domain) => ShownMeasures(domain.Measures).Count > 0 || domain.CrapScore.HasValue;

    // A domain with no letter that the grade does not require and that has nothing to show has nothing to say.
    private static bool IsListed(DomainView domain) => domain.Grade.HasValue || domain.Required || HasDetails(domain);

    // Every domain whose letter is the overall one sets the grade, several when they tie; nothing
    // limits an A. A grade computed on part of its measures has its limiting domain all the same.
    private bool IsLimiting(DomainView domain) =>
        (SealBlockedByTests && domain.CodeAndTests)
        || (Grade is { OverallGrade: > AnalysisGrade.A }
            && domain.Grade == Grade.OverallGrade);

    /// <summary>
    /// Recette R1-60: the one measure the headline names as the cause of the grade: the first measure, in
    /// the domains' display order, whose letter is the overall one. Null for an A or a grade no measure
    /// explains (it then reads from the breakdown below).
    /// </summary>
    internal static AnalysisGradeMeasureDto? FirstLimitingMeasure(AnalysisGradeSummaryDto? grade)
    {
        if (grade is not { OverallGrade: > AnalysisGrade.A }) return null;
        AnalysisGradeDomain[] order =
        [
            AnalysisGradeDomain.Security, AnalysisGradeDomain.Reliability, AnalysisGradeDomain.CodeQuality,
            AnalysisGradeDomain.Architecture, AnalysisGradeDomain.Performance
        ];
        return order
            .SelectMany(domain => grade.Domains.Where(candidate => candidate.Domain == domain))
            .SelectMany(domain => ShownMeasures(domain.Measures))
            .FirstOrDefault(measure => measure.Grade == grade.OverallGrade);
    }

    /// <summary>Recette R1-61: a count at zero says nothing; the tested lines and the most complex path
    /// stay even at zero, and a measure never observed stays to say so.</summary>
    internal static bool IsShown(AnalysisGradeMeasureDto measure) =>
        measure.Key is "grade.coverage.line" or "grade.complexity.maximum"
        || !measure.ObservedValue.HasValue
        || measure.ObservedValue.Value != 0
        || measure.Grade is > AnalysisGrade.A;

    /// <summary>
    /// Recette R-2: the measures a domain lists. The security score over the open findings
    /// (<c>grade.security.findings</c>) is left out when a severity line shown beside it already says the same:
    /// the same number of findings (so every open finding sits in the severity lines) and the same letter. Kept
    /// whenever it adds something (a low or info finding no severity line counts, or another letter), because
    /// it is the measure that grades the domain. Display only: the grade is computed as before.
    /// </summary>
    internal static IReadOnlyList<AnalysisGradeMeasureDto> ShownMeasures(IReadOnlyList<AnalysisGradeMeasureDto> measures)
    {
        var shown = measures.Where(IsShown).ToList();
        return SeverityLinesFromTheWorst([.. shown.Where(measure => !RepeatsASeverityLine(measure, shown))]);
    }

    // The severity lines read from the worst to the mildest (critical, high, medium, low, info), whatever the
    // alphabetical order of their keys; every other line keeps its place.
    private static List<AnalysisGradeMeasureDto> SeverityLinesFromTheWorst(List<AnalysisGradeMeasureDto> shown)
    {
        var slots = Enumerable.Range(0, shown.Count)
            .Where(index => shown[index] is { Severity: not null, FindingCounts: null })
            .ToList();
        var ordered = slots.Select(index => shown[index]).OrderByDescending(measure => measure.Severity!.Value).ToList();
        for (var position = 0; position < slots.Count; position++) shown[slots[position]] = ordered[position];
        return shown;
    }

    /// <summary>The required measures nobody observed, over every domain of the grade (recettes R-73, R-74): the grade
    /// is the one of the other measures, and each of these says so on its line.</summary>
    internal static int MissingMeasureCount(AnalysisGradeSummaryDto? grade) =>
        grade?.Domains.Sum(domain => domain.Measures.Count(IsMissing)) ?? 0;

    /// <summary>
    /// A letter computed on part of its required measures (the completeness flag the sealed candidate contract reads):
    /// such a grade does not qualify a release, whose deployment then asks for the extra approval. A grade with no letter
    /// at all is not partial, nothing was graded.
    /// </summary>
    internal static bool IsPartialGrade(AnalysisGradeSummaryDto? grade) =>
        grade is { OverallGrade: not null, Completeness: AnalysisGradeCompleteness.Incomplete };

    /// <summary>A measure the grade requires and that no evidence fed.</summary>
    internal static bool IsMissing(AnalysisGradeMeasureDto measure) => measure is { Required: true, Observed: false };

    /// <summary>The value column of a measure: its value, that it was not measured when it is required and missing, or
    /// that the grading validator rejected its rule (run #2958, an F whose reason reads in the hint).</summary>
    private string MeasureValue(AnalysisGradeMeasureDto measure) =>
        measure.Rejection is not null ? L["GradeMeasureRejected"].Value
        : IsMissing(measure) ? L["GradeMeasureNotMeasured"].Value
        : MeasureText(measure);

    /// <summary>"Evidence expected from the step X": the pipeline step that publishes the evidence of a missing measure;
    /// null when the pipeline names none (the line still says "not measured").</summary>
    private string? MissingHint(AnalysisGradeMeasureDto measure) =>
        string.IsNullOrWhiteSpace(measure.ProducerStep)
            ? null
            : $"{L["GradeMeasureMissingStep"].Value} {measure.ProducerStep}";

    private static bool RepeatsASeverityLine(AnalysisGradeMeasureDto measure, IReadOnlyList<AnalysisGradeMeasureDto> shown)
    {
        if (measure.Key != "grade.security.findings" || measure.FindingCounts is not { } counts || !measure.Grade.HasValue) return false;
        var severityLines = shown.Where(other => other.Severity.HasValue && other.ObservedValue.HasValue).ToList();
        return severityLines.Count > 0
            && severityLines.Sum(line => line.ObservedValue!.Value) == AnalysisFindingScore.Total(counts)
            && severityLines.Any(line => line.Grade == measure.Grade);
    }

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
                    codeAndTests.Where(domain => domain.EvaluatedAt.HasValue)
                        .Select(domain => domain.EvaluatedAt)
                        .Min(),
                    codeAndTests.SelectMany(domain => domain.Measures).ToArray(),
                    CrapScore,
                    "AnalysisCriterionCodeQualityAndTests",
                    CodeAndTests: true));
            }

            AddDomain(result, source, AnalysisGradeDomain.Architecture);
            // Show runtime performance only when a measurement has been observed.
            if (source.Any(domain => domain.Domain == AnalysisGradeDomain.Performance
                    && domain.Measures.Any(measure => measure.Observed)))
                AddDomain(result, source, AnalysisGradeDomain.Performance);
            return [.. result.Where(IsListed)];
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

    // The resource that names each grade rule; a table rather than a switch so that adding a rule does not raise
    // the cyclomatic complexity of the lookup (the repository guard holds every method to grade A).
    private static readonly IReadOnlyDictionary<string, string> MeasureLabelResources = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["grade.security.critical"] = "AnalysisCriticalVulnerabilities",
        ["grade.security.high"] = "AnalysisHighVulnerabilities",
        ["grade.security.medium"] = "AnalysisMediumVulnerabilities",
        ["grade.security.low"] = "AnalysisLowVulnerabilities",
        ["grade.security.info"] = "AnalysisInfoFindings",
        ["grade.architecture.critical"] = "AnalysisArchitectureCritical",
        ["grade.architecture.high"] = "AnalysisArchitectureHigh",
        ["grade.architecture.medium"] = "AnalysisArchitectureMedium",
        ["grade.architecture.low"] = "AnalysisArchitectureLow",
        ["grade.architecture.info"] = "AnalysisArchitectureInfo",
        // Show open finding counts in the security and quality gates.
        ["grade.security.findings"] = "AnalysisOpenFindingCount",
        ["grade.quality.findings"] = "AnalysisOpenFindingCount",
        ["grade.coverage.line"] = "AnalysisTestedLines",
        ["grade.coverage.branch"] = "AnalysisTestedBranches",
        ["grade.complexity.maximum"] = "AnalysisMaximumComplexity",
        ["grade.duplication"] = "AnalysisDuplicatedCode",
        ["grade.architecture.cycles"] = "AnalysisDependencyCycles",
        // The measure that stands for a whole grading block the validator rejected (run #2958).
        ["analysis_grading"] = "AnalysisGradingBlock"
    };

    internal static string MeasureLabel(IStringLocalizer<AppStrings> L, string key)
    {
        if (MeasureLabelResources.TryGetValue(key, out var resource)) return L[resource];
        return key.StartsWith("grade.", StringComparison.OrdinalIgnoreCase)
            ? key["grade.".Length..].Replace('.', ' ')
            : key;
    }

    internal static string MeasureText(AnalysisGradeMeasureDto measure)
    {
        if (!measure.ObservedValue.HasValue) return "-";
        // A finding score shows the number of open findings, the number the list shows.
        if (measure.FindingCounts is { } counts) return AnalysisFindingScore.Total(counts).ToString(CultureInfo.CurrentCulture);
        var value = measure.ObservedValue.Value.ToString("0.##");
        if (string.Equals(measure.Unit, "percent", StringComparison.OrdinalIgnoreCase)) return $"{value} %";
        return string.IsNullOrWhiteSpace(measure.Unit) ? value : $"{value} {measure.Unit}";
    }

    private static string CriterionKey(AnalysisGradeDomain domain) => domain switch
    {
        AnalysisGradeDomain.Security => "AnalysisCriterionSecurity",
        AnalysisGradeDomain.Architecture => "AnalysisCriterionArchitecture",
        AnalysisGradeDomain.Performance => "AnalysisCriterionPerformance",
        _ => "AnalysisCriterionCodeQualityAndTests"
    };

    /// <summary>The criterion of a measure in words, for its tooltip and detail.</summary>
    private string MeasureCriterion(string key) => key switch
    {
        "grade.coverage.line" => L["AnalysisCriterionCoverageLine"],
        "grade.coverage.branch" => L["AnalysisCriterionCoverageBranch"],
        "grade.complexity.maximum" => L["AnalysisCriterionComplexityMaximum"],
        "grade.duplication" => L["AnalysisCriterionDuplication"],
        "grade.architecture.cycles" => L["AnalysisCriterionCycles"],
        "grade.security.critical" or "grade.security.high" or "grade.security.medium" or "grade.security.low"
            => L["AnalysisCriterionVulnerabilities"],
        "grade.security.info"
            or "grade.architecture.critical" or "grade.architecture.high" or "grade.architecture.medium"
            or "grade.architecture.low" or "grade.architecture.info"
            => L["AnalysisCriterionSeverityFindings"],
        "grade.security.findings" or "grade.quality.findings" => L["AnalysisCriterionFindings"],
        _ => L["AnalysisCriterionGeneric"]
    };

    /// <summary>The grade thresholds of a measure, in the direction that is better ("A ≥ 75 %, B ≥ 65 %"); null
    /// when the measure carries none.</summary>
    internal static string? ThresholdText(AnalysisGradeMeasureDto measure)
    {
        var sign = measure.Direction == AnalysisMetricDirection.LowerIsBetter ? "≤" : "≥";
        var suffix = string.Equals(measure.Unit, "percent", StringComparison.OrdinalIgnoreCase) ? " %" : string.Empty;
        var bands = new (string Letter, double? Value)[]
        {
            ("A", measure.AThreshold), ("B", measure.BThreshold), ("C", measure.CThreshold),
            ("D", measure.DThreshold), ("E", measure.EThreshold)
        };
        var defined = bands.TakeWhile(part => part.Value.HasValue).ToArray();
        if (defined.Length == 0) return null;
        string Number(double value) => value.ToString("0.##", CultureInfo.CurrentCulture) + suffix;
        var parts = defined.Select(part => $"{part.Letter} {sign} {Number(part.Value!.Value)}").ToList();
        // A rule on findings can stop before E: everything beyond its last threshold is the next grade
        // ("A ≤ 0, B ≤ 5, C > 5"), never worse (recette R-73).
        if (defined.Length < bands.Length)
        {
            var opposite = measure.Direction == AnalysisMetricDirection.LowerIsBetter ? ">" : "<";
            parts.Add($"{bands[defined.Length].Letter} {opposite} {Number(defined[^1].Value!.Value)}");
        }
        return string.Join(", ", parts);
    }

    private void AddDomain(
        ICollection<DomainView> target,
        IReadOnlyList<AnalysisGradeDomainDto> source,
        AnalysisGradeDomain domain)
    {
        var item = source.FirstOrDefault(candidate => candidate.Domain == domain);
        if (item is null) return;
        target.Add(new DomainView(
            DomainLabel(L, domain), item.Grade, item.Required, item.EvaluatedAt, item.Measures, null,
            CriterionKey(domain)));
    }

    private static AnalysisGrade? WorstGrade(IEnumerable<AnalysisGradeDomainDto> domains) =>
        domains.Where(domain => domain.Grade.HasValue)
            .Select(domain => domain.Grade)
            .Max();

    private sealed record DomainView(
        string Label,
        AnalysisGrade? Grade,
        bool Required,
        DateTime? EvaluatedAt,
        IReadOnlyList<AnalysisGradeMeasureDto> Measures,
        double? CrapScore,
        string CriterionKey,
        bool CodeAndTests = false);
}
