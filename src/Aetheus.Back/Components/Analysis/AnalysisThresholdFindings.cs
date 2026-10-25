// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// ADR-056, "every detected problem is a finding": a metric rule the analysis crosses (coverage under its
/// minimum, duplication over its maximum, a measure that got worse than in the previous analysis) produces
/// a finding, the unit to fix, track and decide, instead of an alert with no identity.
///
/// Only aggregate rows (project or language scope) produce one. The analysis receives coverage and
/// duplication per report (one per tool, language and report file), so the finding is per module, the
/// module being the report; a row about one file, one symbol or one dependency edge would flood the list and
/// has no actionable unit here (cycles and complex methods are findings of their own, published by the
/// agent with their members and symbol).
///
/// The finding's identity is the rule, the measure, its scope, language, scanner and module, never the
/// value: it is the same finding from one run to the next while the rule stays crossed, and it is marked
/// fixed when the measure is back inside its threshold.
/// </summary>
internal static class AnalysisThresholdFindings
{
    /// <summary>The rule id of every finding born of a crossed metric rule; the policy key follows.</summary>
    public const string RulePrefix = "threshold.metric.";

    public static bool IsThresholdRule(string? ruleId) =>
        ruleId?.StartsWith(RulePrefix, StringComparison.Ordinal) == true;

    /// <summary>True when the crossing of this rule by this metric was decided on: the finding it produced is
    /// accepted, a false positive or mitigated. The rule then neither warns nor blocks for it.</summary>
    public static bool IsDecided(AnalysisReport report, AnalysisPolicy policy, AnalysisMetric metric)
    {
        if (!IsAggregate(metric)) return false;
        var fingerprint = Fingerprint(report, policy, metric);
        return report.Occurrences.Any(occurrence =>
            string.Equals(occurrence.AnalysisFinding.Fingerprint, fingerprint, StringComparison.Ordinal)
            && occurrence.AnalysisFinding.Status.IsDecision());
    }

    internal static bool IsAggregate(AnalysisMetric metric) =>
        metric.FilePath is null
        && metric.Symbol is null
        && (metric.Scope is null
            || string.Equals(metric.Scope, "project", StringComparison.OrdinalIgnoreCase)
            || string.Equals(metric.Scope, "language", StringComparison.OrdinalIgnoreCase));

    /// <summary>The module of a report: the file it was published from (a coverage report per product), else
    /// nothing.</summary>
    private static string? ModuleOf(AnalysisReport report) =>
        string.IsNullOrWhiteSpace(report.StepName)
            ? string.IsNullOrWhiteSpace(report.ReportPath) ? null : report.ReportPath
            : report.StepName;

    internal static string Fingerprint(AnalysisReport report, AnalysisPolicy policy, AnalysisMetric metric) =>
        Hash(string.Join('|',
            "v2",
            "threshold",
            AnalysisPolicyKey.ForExisting(policy).ToLowerInvariant(),
            metric.Key.ToLowerInvariant(),
            (metric.Scope ?? string.Empty).ToLowerInvariant(),
            (metric.Language ?? string.Empty).ToLowerInvariant(),
            report.ScannerKey.ToLowerInvariant(),
            (ModuleOf(report) ?? string.Empty).ToLowerInvariant()));

    internal static ParsedAnalysisFinding Build(AnalysisReport report, AnalysisPolicy policy, AnalysisMetric metric)
    {
        var policyKey = AnalysisPolicyKey.ForExisting(policy);
        var fingerprint = Fingerprint(report, policy, metric);
        var module = ModuleOf(report);
        var where = string.Join(", ", new[] { metric.Language, module }.Where(item => !string.IsNullOrWhiteSpace(item)));
        var scope = where.Length == 0 ? string.Empty : $" ({where})";
        var unit = string.IsNullOrWhiteSpace(metric.Unit) ? string.Empty : $" {metric.Unit}";
        var (verb, reason) = Describe(policy, metric);
        return new ParsedAnalysisFinding
        {
            Fingerprint = fingerprint,
            LocationHash = Hash($"{fingerprint}|{report.ScannerKey}"),
            ToolName = Truncate(metric.ToolName, 200),
            RuleId = Truncate(RulePrefix + policyKey, 300),
            Category = report.Category,
            Severity = AnalysisSeverity.Medium,
            Confidence = AnalysisConfidence.High,
            Title = Truncate($"{metric.Key}{scope} {verb}", 500),
            Message = Truncate(
                $"{metric.Key}{scope} is {Format(metric.Value)}{unit}: {reason} (rule {policyKey}).", 4000),
            Symbol = Truncate(module ?? metric.Key, 500)
        };
    }

    private static (string Verb, string Reason) Describe(AnalysisPolicy policy, AnalysisMetric metric)
    {
        var threshold = policy.Threshold.HasValue ? Format(policy.Threshold.Value) : "-";
        return policy.Operator switch
        {
            AnalysisPolicyOperator.Degraded => ("got worse than in the previous analysis",
                $"it was {(metric.BaselineValue.HasValue ? Format(metric.BaselineValue.Value) : "-")} in the previous analysis"),
            AnalysisPolicyOperator.LessThan or AnalysisPolicyOperator.LessThanOrEqual =>
                ("is below its threshold", $"the threshold is {threshold}"),
            AnalysisPolicyOperator.GreaterThan or AnalysisPolicyOperator.GreaterThanOrEqual =>
                ("is above its threshold", $"the threshold is {threshold}"),
            _ => ("breaks its rule", $"the rule expects {threshold}")
        };
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Truncate(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
