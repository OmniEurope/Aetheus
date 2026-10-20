// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

public sealed class AnalysisPolicyEngine(IAnalysisRepository repository)
{
    public Task<AnalysisEvaluation> EvaluateAsync(
        AnalysisReport report,
        AnalysisRunContext context,
        DateTime now,
        CancellationToken ct = default) =>
        EvaluateAsync(report, context, now, flaggedMetrics: null, ct);

    /// <summary>Evaluates the report and adds threshold-breaking per-file and per-symbol metrics to
    /// <paramref name="flaggedMetrics"/> when supplied. These rows remain eligible for retention
    /// beyond the worst-per-key selection.</summary>
    public async Task<AnalysisEvaluation> EvaluateAsync(
        AnalysisReport report,
        AnalysisRunContext context,
        DateTime now,
        ICollection<AnalysisMetric>? flaggedMetrics,
        CancellationToken ct = default)
    {
        var grade = AnalysisGradeEngine.Evaluate(report, context, now);
        if (report.Status is AnalysisReportStatus.Error or AnalysisReportStatus.TimedOut
            or AnalysisReportStatus.Unavailable)
            return ErrorEvaluation(report, context, grade, now);

        var candidates = await repository.GetApplicablePoliciesAsync(
            context.OrganizationId,
            context.ProjectId,
            ct).ConfigureAwait(false);
        var policies = AnalysisPolicyResolver.Resolve(
            context.OrganizationId,
            context.ProjectId,
            candidates);
        policies = PipelineAnalysisGatePolicyResolver.Apply(
            policies,
            context.PipelineYaml,
            report.Category,
            now);
        var exceptions = await repository.GetActiveExceptionsAsync(
            context.ProjectId,
            now,
            ct).ConfigureAwait(false);
        var blockerCount = 0;
        var warningCount = 0;
        var decisions = new AnalysisPolicyDecisionSnapshotBuilder();

        foreach (var occurrence in report.Occurrences
                     .GroupBy(item => item.AnalysisFinding.Fingerprint, StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            var counts = EvaluateFinding(occurrence, report, context, policies, exceptions, decisions);
            blockerCount += counts.Blockers;
            warningCount += counts.Warnings;
        }

        foreach (var metric in report.Metrics)
        {
            var counts = EvaluateMetric(metric, report, context, policies, exceptions, decisions);
            if (counts.Blockers + counts.Warnings > 0) flaggedMetrics?.Add(metric);
            blockerCount += counts.Blockers;
            warningCount += counts.Warnings;
        }

        var status = blockerCount > 0
            ? AnalysisGateStatus.Blocked
            : warningCount > 0
                ? AnalysisGateStatus.Warning
                : AnalysisGateStatus.Passed;
        var snapshot = decisions.Build();
        return new AnalysisEvaluation
        {
            OrganizationId = context.OrganizationId,
            ProjectId = context.ProjectId,
            AnalysisReportId = report.Id,
            PipelineRunId = context.PipelineRunId,
            Status = status,
            PolicySnapshotJson = snapshot,
            PolicySnapshotHash = AnalysisPolicySnapshot.Hash(snapshot),
            Grade = grade.Grade,
            GradeCompleteness = grade.Completeness,
            GradeSnapshotJson = grade.SnapshotJson,
            GradeSnapshotHash = grade.SnapshotHash,
            BlockerCount = blockerCount,
            WarningCount = warningCount,
            EvaluatedAt = now
        };
    }

    private static PolicyCounts EvaluateFinding(
        AnalysisFindingOccurrence occurrence, AnalysisReport report, AnalysisRunContext context,
        IReadOnlyList<ResolvedAnalysisPolicy> policies, IReadOnlyList<AnalysisPolicyException> exceptions,
        AnalysisPolicyDecisionSnapshotBuilder decisions)
    {
        var finding = occurrence.AnalysisFinding;
        var matchingExceptions = exceptions.Where(item => MatchesException(
            item, finding, occurrence, report, context.BranchName, context.EnvironmentName)).ToList();
        // An exception without a policy covers every policy; one naming a policy covers only that one.
        var exception = matchingExceptions.FirstOrDefault(item => !item.AnalysisPolicyId.HasValue);
        if (exception is not null)
        {
            decisions.AddFinding($"exception:{exception.Id}", FindingDecisionKey(occurrence), "excepted", exception.ExpiresAt, null);
            return default;
        }
        var matched = policies.Where(policy => policy.IsEffective && MatchesPolicy(
            policy.Policy, finding, occurrence, report, context.BranchName, context.EnvironmentName)).ToList();
        if (matched.Count == 0)
        {
            decisions.AddFinding("no-policy", FindingDecisionKey(occurrence), "pass", null, null);
            return default;
        }
        var counts = default(PolicyCounts);
        foreach (var policy in matched)
        {
            var scoped = PolicyException(matchingExceptions, policy);
            if (scoped is not null)
            {
                decisions.AddFinding($"exception:{scoped.Id}", FindingDecisionKey(occurrence), "excepted",
                    scoped.ExpiresAt, AnalysisPolicySnapshot.Serialize(policy));
                continue;
            }
            counts = counts.Add(policy.Policy.Behavior);
            decisions.AddFinding(Source(policy), FindingDecisionKey(occurrence), policy.Policy.Behavior.ToString(),
                null, AnalysisPolicySnapshot.Serialize(policy), finding.Severity.ToString(),
                policy.Policy.SeverityThreshold?.ToString(), AnalysisPolicyOperator.GreaterThanOrEqual.ToString());
        }
        return counts;
    }

    private static PolicyCounts EvaluateMetric(
        AnalysisMetric metric, AnalysisReport report, AnalysisRunContext context,
        IReadOnlyList<ResolvedAnalysisPolicy> policies, IReadOnlyList<AnalysisPolicyException> exceptions,
        AnalysisPolicyDecisionSnapshotBuilder decisions)
    {
        var matchingExceptions = exceptions.Where(item => MatchesMetricException(
            item, report, context.BranchName, context.EnvironmentName)).ToList();
        var exception = matchingExceptions.FirstOrDefault(item => !item.AnalysisPolicyId.HasValue);
        if (exception is not null)
        {
            decisions.AddMetric($"exception:{exception.Id}", metric.Key, "excepted", exception.ExpiresAt, null, MetricText(metric.Value));
            return default;
        }
        var matched = policies.Where(policy => policy.IsEffective && MatchesMetricPolicy(
            policy.Policy, metric, report, context.BranchName, context.EnvironmentName)).ToList();
        if (matched.Count == 0)
        {
            decisions.AddMetric("no-policy", metric.Key, "pass", null, null, MetricText(metric.Value));
            return default;
        }
        var counts = default(PolicyCounts);
        foreach (var policy in SupersedeCatchAllDegradation(matched).Where(policy => MetricThresholdMatches(policy.Policy, metric)))
        {
            var scoped = PolicyException(matchingExceptions, policy);
            if (scoped is not null)
            {
                decisions.AddMetric($"exception:{scoped.Id}", metric.Key, "excepted", scoped.ExpiresAt,
                    AnalysisPolicySnapshot.Serialize(policy), MetricText(metric.Value));
                continue;
            }
            counts = counts.Add(policy.Policy.Behavior);
            decisions.AddMetric(Source(policy), metric.Key, policy.Policy.Behavior.ToString(), null,
                AnalysisPolicySnapshot.Serialize(policy), MetricText(metric.Value),
                EvidenceThreshold(policy.Policy, metric),
                policy.Policy.Operator?.ToString());
        }
        return counts;
    }

    /// <summary>The value the metric was held against. A degradation policy has no threshold of its own:
    /// it compares with the previous analysis, so that previous value is what the evidence records and
    /// what the gate shows next to the observed one.</summary>
    private static string? EvidenceThreshold(AnalysisPolicy policy, AnalysisMetric metric)
    {
        if (policy.Operator == AnalysisPolicyOperator.Degraded && metric.BaselineValue.HasValue)
            return MetricText(metric.BaselineValue.Value);
        return policy.Threshold.HasValue ? MetricText(policy.Threshold.Value) : null;
    }

    /// <summary>A degradation policy naming the metric (no wildcard) replaces the catch-all one for it, so a
    /// metric that got worse is one alert and not one per degradation policy.</summary>
    private static IEnumerable<ResolvedAnalysisPolicy> SupersedeCatchAllDegradation(
        IReadOnlyList<ResolvedAnalysisPolicy> matched)
    {
        var namesTheMetric = matched.Any(policy => IsDegradation(policy.Policy) && policy.Policy.MetricKey != "*");
        return namesTheMetric
            ? matched.Where(policy => !IsDegradation(policy.Policy) || policy.Policy.MetricKey != "*")
            : matched;
    }

    private static bool IsDegradation(AnalysisPolicy policy) => policy.Operator == AnalysisPolicyOperator.Degraded;

    private static AnalysisPolicyException? PolicyException(
        IReadOnlyList<AnalysisPolicyException> matchingExceptions, ResolvedAnalysisPolicy policy) =>
        matchingExceptions.FirstOrDefault(item => item.AnalysisPolicyId == policy.Policy.Id);

    private readonly record struct PolicyCounts(int Blockers, int Warnings)
    {
        public PolicyCounts Add(AnalysisGateBehavior behavior) =>
            behavior == AnalysisGateBehavior.Block
                ? this with { Blockers = Blockers + 1 }
                : this with { Warnings = Warnings + 1 };
    }

    private static string FindingDecisionKey(AnalysisFindingOccurrence occurrence) =>
        $"{occurrence.RuleId}:{occurrence.AnalysisFinding.Fingerprint}";

    private static AnalysisEvaluation ErrorEvaluation(
        AnalysisReport report,
        AnalysisRunContext context,
        AnalysisGradeEvaluation grade,
        DateTime now)
    {
        const string snapshot = "[{\"source\":\"scanner\",\"outcome\":\"error\"}]";
        return new AnalysisEvaluation
        {
            OrganizationId = context.OrganizationId,
            ProjectId = context.ProjectId,
            AnalysisReportId = report.Id,
            PipelineRunId = context.PipelineRunId,
            Status = AnalysisGateStatus.Error,
            PolicySnapshotJson = snapshot,
            PolicySnapshotHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(snapshot))),
            Grade = grade.Grade,
            GradeCompleteness = grade.Completeness,
            GradeSnapshotJson = grade.SnapshotJson,
            GradeSnapshotHash = grade.SnapshotHash,
            BlockerCount = 1,
            EvaluatedAt = now
        };
    }

    private static string Source(ResolvedAnalysisPolicy policy) =>
        $"policy:{AnalysisPolicyKey.ForExisting(policy.Policy)}:{policy.Scope}:v{policy.Policy.Version}";

    private static string MetricText(double value) =>
        value.ToString("G17", CultureInfo.InvariantCulture);

    private static bool MatchesMetricPolicy(
        AnalysisPolicy policy,
        AnalysisMetric metric,
        AnalysisReport report,
        string? branch,
        string? environment)
    {
        if (string.IsNullOrWhiteSpace(policy.MetricKey)
            || !MatchesPattern(policy.MetricKey, metric.Key)) return false;
        if (policy.Category.HasValue && policy.Category.Value != report.Category) return false;
        if (!string.IsNullOrWhiteSpace(policy.ScannerKey)
            && !string.Equals(policy.ScannerKey, report.ScannerKey, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(policy.BranchPattern)
            && !MatchesPattern(policy.BranchPattern, branch ?? string.Empty)) return false;
        return string.IsNullOrWhiteSpace(policy.EnvironmentPattern)
            || MatchesPattern(policy.EnvironmentPattern, environment ?? string.Empty);
    }

    private static bool MetricThresholdMatches(AnalysisPolicy policy, AnalysisMetric metric)
    {
        if (!policy.Operator.HasValue || !policy.Threshold.HasValue) return false;
        var threshold = policy.Threshold.Value;
        return policy.Operator.Value switch
        {
            AnalysisPolicyOperator.GreaterThan => metric.Value > threshold,
            AnalysisPolicyOperator.GreaterThanOrEqual => metric.Value >= threshold,
            AnalysisPolicyOperator.LessThan => metric.Value < threshold,
            AnalysisPolicyOperator.LessThanOrEqual => metric.Value <= threshold,
            AnalysisPolicyOperator.Equals => Math.Abs(metric.Value - threshold) < 0.000_001,
            AnalysisPolicyOperator.Changed => metric.BaselineValue.HasValue
                && Math.Abs(metric.Value - metric.BaselineValue.Value) >= Math.Abs(threshold),
            AnalysisPolicyOperator.Degraded => metric.Direction != AnalysisMetricDirection.Informational
                && metric.BaselineValue.HasValue
                && (metric.Direction == AnalysisMetricDirection.HigherIsBetter
                    ? metric.Value < metric.BaselineValue.Value
                    : metric.Value > metric.BaselineValue.Value),
            _ => false
        };
    }

    private static bool MatchesMetricException(
        AnalysisPolicyException exception,
        AnalysisReport report,
        string? branch,
        string? environment)
    {
        if (exception.AnalysisFindingId.HasValue || !string.IsNullOrWhiteSpace(exception.Fingerprint)
            || !string.IsNullOrWhiteSpace(exception.RuleId)) return false;
        if (exception.Category.HasValue && exception.Category.Value != report.Category) return false;
        if (!string.IsNullOrWhiteSpace(exception.ScannerKey)
            && !string.Equals(exception.ScannerKey, report.ScannerKey, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(exception.BranchPattern)
            && !MatchesPattern(exception.BranchPattern, branch ?? string.Empty)) return false;
        if (!string.IsNullOrWhiteSpace(exception.EnvironmentPattern)
            && !MatchesPattern(exception.EnvironmentPattern, environment ?? string.Empty)) return false;
        return exception.Category.HasValue || !string.IsNullOrWhiteSpace(exception.ScannerKey);
    }

    private static bool MatchesPolicy(
        AnalysisPolicy policy,
        AnalysisFinding finding,
        AnalysisFindingOccurrence occurrence,
        AnalysisReport report,
        string? branch,
        string? environment)
    {
        if (policy.MetricKey is not null) return false;
        if (policy.NewFindingsOnly && !occurrence.IsNew) return false;
        if (policy.Category.HasValue && policy.Category.Value != finding.Category) return false;
        if (!string.IsNullOrWhiteSpace(policy.ScannerKey)
            && !string.Equals(policy.ScannerKey, report.ScannerKey, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(policy.RuleId)
            && !string.Equals(policy.RuleId, occurrence.RuleId, StringComparison.OrdinalIgnoreCase)) return false;
        if (policy.SeverityThreshold.HasValue && finding.Severity < policy.SeverityThreshold.Value) return false;
        return MatchesPolicyScope(policy, branch, environment);
    }

    private static bool MatchesPolicyScope(
        AnalysisPolicy policy, string? branch, string? environment)
    {
        if (!string.IsNullOrWhiteSpace(policy.BranchPattern)
            && !MatchesPattern(policy.BranchPattern, branch ?? string.Empty)) return false;
        return string.IsNullOrWhiteSpace(policy.EnvironmentPattern)
            || MatchesPattern(policy.EnvironmentPattern, environment ?? string.Empty);
    }

    private static bool MatchesException(
        AnalysisPolicyException exception,
        AnalysisFinding finding,
        AnalysisFindingOccurrence occurrence,
        AnalysisReport report,
        string? branch,
        string? environment)
    {
        if (exception.Category.HasValue && exception.Category.Value != finding.Category) return false;
        if (!string.IsNullOrWhiteSpace(exception.ScannerKey)
            && !string.Equals(exception.ScannerKey, report.ScannerKey, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(exception.BranchPattern)
            && !MatchesPattern(exception.BranchPattern, branch ?? string.Empty)) return false;
        if (!string.IsNullOrWhiteSpace(exception.EnvironmentPattern)
            && !MatchesPattern(exception.EnvironmentPattern, environment ?? string.Empty)) return false;
        var scoped = exception.AnalysisFindingId.HasValue && exception.AnalysisFindingId.Value == finding.Id;
        scoped |= !string.IsNullOrWhiteSpace(exception.Fingerprint)
            && string.Equals(exception.Fingerprint, finding.Fingerprint, StringComparison.Ordinal);
        scoped |= !string.IsNullOrWhiteSpace(exception.RuleId)
            && string.Equals(exception.RuleId, occurrence.RuleId, StringComparison.OrdinalIgnoreCase);
        scoped |= !string.IsNullOrWhiteSpace(exception.ScannerKey) || exception.Category.HasValue;
        return scoped;
    }

    private static bool MatchesPattern(string pattern, string value)
    {
        if (pattern == "*") return true;
        if (!pattern.Contains('*', StringComparison.Ordinal))
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
        var parts = pattern.Split('*');
        var position = 0;
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            var index = value.IndexOf(part, position, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            position = index + part.Length;
        }
        return (pattern.StartsWith('*') || value.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase))
            && (pattern.EndsWith('*') || value.EndsWith(parts[^1], StringComparison.OrdinalIgnoreCase));
    }
}
