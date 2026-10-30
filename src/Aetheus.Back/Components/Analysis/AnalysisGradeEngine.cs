// SPDX-License-Identifier: EUPL-1.2
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

internal static class AnalysisGradeEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AnalysisGradeEvaluation Evaluate(
        AnalysisReport report,
        AnalysisRunContext context,
        DateTime now)
    {
        var resolved = Resolve(context.PipelineYaml, report.Category);
        if (resolved is null)
            return AnalysisGradeEvaluation.Empty;
        var (grading, stages, scope) = resolved.Value;

        PipelineAnalysisGradingValidator.TryParseGrade(grading.MinimumGrade, out var minimumGrade);
        var requiredDomains = grading.RequiredDomains
            .Select(value => PipelineAnalysisGradingValidator.TryParseDomain(value, out var domain)
                ? domain
                : null)
            .Where(domain => domain.HasValue)
            .Select(domain => domain!.Value)
            .ToHashSet();

        // Owner decision after run #2958: the valid rules are always graded, and each rule the validator rejects is
        // an F carrying the validator's message, as is a block that cannot be used at all. A problem counts, so the
        // domain of a rejected measure counts in the overall grade.
        var diagnosis = PipelineAnalysisGradingValidator.Diagnose(scope, grading);
        var evidenceAvailable = report.Status is AnalysisReportStatus.Passed or AnalysisReportStatus.Failed;
        var measures = grading.Rules
            .Take(diagnosis.RuleErrors.Count)
            .Select((rule, index) => (Rule: rule, Errors: diagnosis.RuleErrors[index]))
            .OrderBy(item => item.Rule.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Errors.Count == 0
                ? WithProducerStep(EvaluateRule(item.Rule, report, evidenceAvailable), stages, item.Rule)
                : AnalysisGradeRejections.Rule(item.Rule, item.Errors, scope))
            .ToList();
        if (diagnosis.GradingErrors.Count > 0)
            measures.Add(AnalysisGradeRejections.Grading(diagnosis.GradingErrors, requiredDomains, scope));
        requiredDomains.UnionWith(measures.Where(measure => measure.Rejection is not null).Select(measure => measure.Domain));
        var summary = BuildSummary(
            measures,
            requiredDomains,
            minimumGrade,
            context.CommitHash,
            context.PipelineRunId,
            now);
        var json = Serialize(summary);
        var applicableRequiredMeasures = measures
            .Where(measure => measure.Required
                && (!measure.Category.HasValue || measure.Category.Value == report.Category))
            .ToList();
        // The grade is the worst of the measures that were observed (recette R-73, R-74): a report with no finding
        // is an A, and a measure nobody observed does not hide the others. Completeness only says whether every
        // required measure was observed; the sealed candidate contract reads it, nothing shows it as a grade state.
        var observed = applicableRequiredMeasures
            .Where(measure => measure.Observed && measure.Grade.HasValue)
            .ToList();
        var complete = applicableRequiredMeasures.Count > 0 && observed.Count == applicableRequiredMeasures.Count;
        return new AnalysisGradeEvaluation(
            observed.Count > 0 ? observed.Max(measure => measure.Grade) : null,
            complete ? AnalysisGradeCompleteness.Complete : AnalysisGradeCompleteness.Incomplete,
            json,
            Hash(json));
    }

    public static AnalysisGradeSummaryDto? Aggregate(
        IEnumerable<string> snapshots,
        string? commitHash = null,
        int? pipelineRunId = null)
    {
        var summaries = snapshots
            .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot) && snapshot != "{}")
            .Select(Deserialize)
            .Where(summary => summary is not null)
            .Cast<AnalysisGradeSummaryDto>()
            .ToList();
        if (summaries.Count == 0) return null;

        var requiredDomains = summaries
            .SelectMany(summary => summary.Domains)
            .Where(domain => domain.Required)
            .Select(domain => domain.Domain)
            .ToHashSet();
        var minimumGrade = summaries
            .Where(summary => summary.MinimumGrade.HasValue)
            .Select(summary => summary.MinimumGrade!.Value)
            .Cast<AnalysisGrade?>()
            .OrderBy(grade => grade)
            .FirstOrDefault();
        var measures = summaries
            .SelectMany(summary => summary.Domains)
            .SelectMany(domain => domain.Measures)
            .GroupBy(measure => measure.Key, StringComparer.OrdinalIgnoreCase)
            .Select(AggregateMeasure)
            .OrderBy(measure => measure.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var commits = summaries
            .Select(summary => summary.CommitHash)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var summaryCommit = commitHash ?? (commits.Count == 1 ? commits[0] : null);
        return WithHash(BuildSummary(
            measures,
            requiredDomains,
            minimumGrade,
            summaryCommit,
            pipelineRunId ?? summaries.Select(summary => summary.PipelineRunId).Distinct().SingleOrDefault(),
            summaries.Max(summary => summary.EvaluatedAt)));
    }

    /// <summary>Merge a run and its triggered descendants domain by domain through <see
    /// cref="AnalysisGradeMerge"/>. Compare grades without adding finding counts, which would
    /// duplicate findings reported by multiple runs.</summary>
    public static AnalysisGradeSummaryDto MergeRuns(
        IReadOnlyCollection<AnalysisGradeSummaryDto> runs,
        int pipelineRunId) =>
        WithHash(AnalysisGradeMerge.MergeRuns(runs, pipelineRunId));

    /// <summary>The grading of the pipeline's analysis gate for the report's scope, with the pipeline's stages (to name
    /// the step that produces each measure) and the scope; null when the pipeline grades nothing. A grading the
    /// validator rejects, in part or whole, is returned: <see cref="Evaluate"/> grades its problems F.</summary>
    private static (PipelineAnalysisGradingDefinition Grading, IReadOnlyList<PipelineStageDefinition> Stages, string Scope)? Resolve(
        string yaml,
        AnalysisCategory category)
    {
        var definition = YamlParsingHelper.ParseAndValidate(yaml);
        if (definition is null) return null;
        var scope = AnalysisGateScopes.ForCategory(category);
        var stages = YamlParsingHelper.FlattenJobs(definition);
        var grading = stages
            .SelectMany(stage => stage.Steps)
            .FirstOrDefault(step =>
                string.Equals(step.Type, "analysis-gate", StringComparison.OrdinalIgnoreCase)
                && AnalysisGateScopes.Normalize(step.AnalysisScope) == scope)
            ?.AnalysisGrading;
        return grading is not null ? (grading, stages, scope) : null;
    }

    // The measure names the step that publishes its evidence, so a measure nobody observed can say which one to look at.
    private static AnalysisGradeMeasureDto WithProducerStep(
        AnalysisGradeMeasureDto measure,
        IEnumerable<PipelineStageDefinition> stages,
        PipelineAnalysisGradeRuleDefinition rule) =>
        measure with { ProducerStep = AnalysisGradeProducerSteps.Find(stages, rule) };

    private static AnalysisGradeMeasureDto EvaluateRule(
        PipelineAnalysisGradeRuleDefinition rule,
        AnalysisReport report,
        bool evidenceAvailable)
    {
        PipelineAnalysisGradingValidator.TryParseDomain(rule.Domain, out var domain);
        PipelineAnalysisGradingValidator.TryParseDirection(rule.Direction, out var direction);
        PipelineAnalysisGateRuleValidator.TryParseEnum<AnalysisCategory>(rule.Category, out var category);
        PipelineAnalysisGateRuleValidator.TryParseEnum<AnalysisSeverity>(rule.Severity, out var severity);
        var applies = evidenceAvailable
            && (!category.HasValue || category.Value == report.Category);
        if (PipelineAnalysisGradingValidator.IsFindingScore(rule))
        {
            var counts = applies
                ? AnalysisFindingScore.Count(OpenFindings(report, rule).Select(finding => finding.Severity))
                : null;
            return BuildMeasure(rule, domain!.Value, direction!.Value, category, null,
                counts is null
                    ? null
                    : AnalysisFindingScore.Value(counts, PipelineAnalysisGradingValidator.IsWeightedFindingScore(rule)),
                null, counts);
        }
        var values = new List<double>();
        string? unit = null;

        if (applies && !string.IsNullOrWhiteSpace(rule.Metric))
        {
            var metrics = report.Metrics
                .Where(metric => FileSystemName.MatchesSimpleExpression(
                    rule.Metric,
                    metric.Key,
                    true))
                .ToList();
            values.AddRange(metrics.Select(metric => metric.Value));
            unit = metrics.Select(metric => metric.Unit).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }
        else if (applies && severity.HasValue)
        {
            values.Add(OpenFindings(report, rule).Count(finding => finding.Severity == severity.Value));
        }

        var observed = values.Count > 0;
        double? observedValue = observed
            ? severity.HasValue
                ? values.Sum()
                : direction == AnalysisMetricDirection.HigherIsBetter
                    ? values.Min()
                    : values.Max()
            : null;
        return BuildMeasure(rule, domain!.Value, direction!.Value, category, severity, observedValue, unit, null);
    }

    /// <summary> The findings a grading rule counts, one per fingerprint, and only the open ones. A
    /// finding someone accepted, marked as a false positive or mitigated is decided, not left to
    /// do, and a fixed one is no longer reported (one that comes back is reopened before the report
    /// is graded). </summary>
    private static IEnumerable<AnalysisFinding> OpenFindings(
        AnalysisReport report,
        PipelineAnalysisGradeRuleDefinition rule) =>
        report.Occurrences
            .Where(occurrence => occurrence.AnalysisFinding.Status == AnalysisFindingStatus.Open
                && (!rule.NewFindingsOnly || occurrence.IsNew))
            .Select(occurrence => occurrence.AnalysisFinding)
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal);

    private static AnalysisGradeMeasureDto AggregateMeasure(
        IGrouping<string, AnalysisGradeMeasureDto> group)
    {
        // A rejected rule stays the F it is, with its reason, whatever the other reports say.
        if (group.FirstOrDefault(measure => measure.Rejection is not null) is { } rejected) return rejected;
        var first = group.First();
        var observed = group
            .Where(measure => measure.Observed && measure.ObservedValue.HasValue)
            .ToList();
        if (first.WeightedFindingScore || first.CountedFindingScore)
        {
            // The reports of one run each counted their own findings; the score follows the summed counts.
            var counts = observed.Count == 0
                ? null
                : AnalysisFindingScore.Sum(observed.Select(measure => measure.FindingCounts ?? new AnalysisGradeFindingCountsDto()));
            return BuildMeasure(
                first with { FindingCounts = counts },
                counts is null ? null : AnalysisFindingScore.Value(counts, first.WeightedFindingScore));
        }
        var values = observed.Select(measure => measure.ObservedValue!.Value).ToList();
        double? value = values.Count == 0
            ? null
            : first.Severity.HasValue
                ? values.Sum()
                : first.Direction == AnalysisMetricDirection.HigherIsBetter
                    ? values.Min()
                    : values.Max();
        // Every report of the run evaluates every rule, so the first measure of the group may come from a
        // report that holds no such metric (no value, no unit). The unit is the observed measure's own.
        var unit = observed.Select(measure => measure.Unit).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? first.Unit;
        return BuildMeasure(first with { Unit = unit }, value);
    }

    private static AnalysisGradeMeasureDto BuildMeasure(
        PipelineAnalysisGradeRuleDefinition rule,
        AnalysisGradeDomain domain,
        AnalysisMetricDirection direction,
        AnalysisCategory? category,
        AnalysisSeverity? severity,
        double? observedValue,
        string? unit,
        AnalysisGradeFindingCountsDto? findingCounts) =>
        BuildMeasure(new AnalysisGradeMeasureDto
        {
            Key = rule.Key,
            Domain = domain,
            Required = rule.Required,
            MetricKey = rule.Metric,
            Category = category,
            Severity = severity,
            Unit = unit,
            Direction = direction,
            AThreshold = rule.A,
            BThreshold = rule.B,
            CThreshold = rule.C,
            DThreshold = rule.D,
            EThreshold = rule.E,
            WeightedFindingScore = PipelineAnalysisGradingValidator.IsWeightedFindingScore(rule),
            CountedFindingScore = PipelineAnalysisGradingValidator.IsFindingScore(rule)
                && !PipelineAnalysisGradingValidator.IsWeightedFindingScore(rule),
            FindingCounts = findingCounts
        }, observedValue);

    private static AnalysisGradeMeasureDto BuildMeasure(
        AnalysisGradeMeasureDto source,
        double? observedValue)
    {
        AnalysisGrade? grade = observedValue.HasValue ? Grade(source, observedValue.Value) : null;
        AnalysisGrade? nextGrade = grade is > AnalysisGrade.A
            ? (AnalysisGrade)((int)grade.Value - 1)
            : null;
        var nextThreshold = nextGrade switch
        {
            AnalysisGrade.A => source.AThreshold,
            AnalysisGrade.B => source.BThreshold,
            AnalysisGrade.C => source.CThreshold,
            AnalysisGrade.D => source.DThreshold,
            AnalysisGrade.E => source.EThreshold,
            _ => null
        };
        double? distance = observedValue.HasValue && nextThreshold.HasValue
            ? source.Direction == AnalysisMetricDirection.HigherIsBetter
                ? Math.Max(0, nextThreshold.Value - observedValue.Value)
                : Math.Max(0, observedValue.Value - nextThreshold.Value)
            : null;
        return source with
        {
            Observed = observedValue.HasValue,
            ObservedValue = observedValue,
            Grade = grade,
            NextGrade = nextGrade,
            DistanceToNextGrade = distance
        };
    }

    private static AnalysisGrade Grade(AnalysisGradeMeasureDto measure, double value)
    {
        // One open critical finding makes a weighted finding score not deliverable.
        if (measure.FindingCounts is { Critical: > 0 }) return AnalysisGrade.F;
        // A rule on findings may stop its thresholds after B, C or D (the validator refuses it elsewhere):
        // everything beyond the last one is then the next grade and never worse, so "at least one high
        // finding" is a D and "six medium findings or more" a C (owner decision of 2026-10-25, R-73).
        var open = measure.Direction == AnalysisMetricDirection.HigherIsBetter
            ? double.NegativeInfinity
            : double.PositiveInfinity;
        var thresholds = new[]
        {
            measure.AThreshold!.Value,
            measure.BThreshold ?? open,
            measure.CThreshold ?? open,
            measure.DThreshold ?? open,
            measure.EThreshold ?? open
        };
        for (var index = 0; index < thresholds.Length; index++)
        {
            var matches = measure.Direction == AnalysisMetricDirection.HigherIsBetter
                ? value >= thresholds[index]
                : value <= thresholds[index];
            if (matches) return (AnalysisGrade)index;
        }
        return AnalysisGrade.F;
    }

    private static AnalysisGradeSummaryDto BuildSummary(
        IReadOnlyCollection<AnalysisGradeMeasureDto> measures,
        IReadOnlySet<AnalysisGradeDomain> requiredDomains,
        AnalysisGrade? minimumGrade,
        string? commitHash,
        int? pipelineRunId,
        DateTime? evaluatedAt)
    {
        var domains = Enum.GetValues<AnalysisGradeDomain>()
            .Select(domain =>
            {
                var domainMeasures = measures.Where(measure => measure.Domain == domain).ToList();
                var gradingMeasures = domainMeasures.Where(measure => measure.Required).ToList();
                var required = requiredDomains.Contains(domain);
                // A domain is graded by the measures observed (recette R-73, R-74): no finding is an A, never a
                // missing grade. A required measure nobody observed stays in the list, unobserved, with the step
                // that should have produced it; completeness only records that some are missing.
                var observed = gradingMeasures
                    .Where(measure => measure.Observed && measure.Grade.HasValue)
                    .ToList();
                var complete = gradingMeasures.Count > 0 && observed.Count == gradingMeasures.Count;
                return new AnalysisGradeDomainDto
                {
                    Domain = domain,
                    Grade = observed.Count > 0 ? observed.Max(measure => measure.Grade) : null,
                    Required = required,
                    Completeness = complete
                        ? AnalysisGradeCompleteness.Complete
                        : AnalysisGradeCompleteness.Incomplete,
                    CommitHash = commitHash,
                    EvaluatedAt = evaluatedAt,
                    Measures = domainMeasures
                };
            })
            .ToList();
        return AnalysisGradeMerge.Summarize(domains, minimumGrade, commitHash, pipelineRunId, evaluatedAt);
    }

    private static AnalysisGradeSummaryDto WithHash(AnalysisGradeSummaryDto summary)
    {
        var json = Serialize(summary with { SnapshotHash = string.Empty });
        return summary with { SnapshotHash = Hash(json) };
    }

    private static string Serialize(AnalysisGradeSummaryDto summary) =>
        JsonSerializer.Serialize(summary with { SnapshotHash = string.Empty }, JsonOptions);

    private static AnalysisGradeSummaryDto? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AnalysisGradeSummaryDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Hash(string json) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
}

internal sealed record AnalysisGradeEvaluation(
    AnalysisGrade? Grade,
    AnalysisGradeCompleteness Completeness,
    string SnapshotJson,
    string SnapshotHash)
{
    public static AnalysisGradeEvaluation Empty { get; } =
        new(null, AnalysisGradeCompleteness.Incomplete, "{}", string.Empty);
}
