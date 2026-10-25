// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>What a report's aggregate metrics make of the effective metric rules: the findings the crossed
/// rules produce, and the identities of the findings of the rules no longer crossed, to close.</summary>
internal sealed record AnalysisThresholdOutcome(
    IReadOnlyList<ParsedAnalysisFinding> Crossed,
    IReadOnlyList<string> NoLongerCrossed);

/// <summary>
/// ADR-056: reads a report's aggregate metrics against the metric rules in force (the same ones the gate
/// counts, through <see cref="AnalysisPolicyEngine"/>) and turns the crossed ones into findings built by
/// <see cref="AnalysisThresholdFindings"/>. Kept apart from <see cref="AnalysisThresholdFindings"/> because
/// the policy engine reads that type to know which findings and crossings were decided on, so the two must
/// not depend on each other.
/// </summary>
internal static class AnalysisThresholdEvaluator
{
    public static AnalysisThresholdOutcome Evaluate(
        AnalysisReport report,
        AnalysisRunContext context,
        IReadOnlyList<ResolvedAnalysisPolicy> policies)
    {
        var crossed = new List<ParsedAnalysisFinding>();
        var closed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metric in report.Metrics.Where(AnalysisThresholdFindings.IsAggregate))
            foreach (var (policy, isCrossed) in AnalysisPolicyEngine.MetricRuleStates(
                policies, metric, report, context.BranchName, context.EnvironmentName))
            {
                if (isCrossed) crossed.Add(AnalysisThresholdFindings.Build(report, policy.Policy, metric));
                else closed.Add(AnalysisThresholdFindings.Fingerprint(report, policy.Policy, metric));
            }
        // A rule crossed by one row of the report is not closed by another row of the same identity.
        closed.ExceptWith(crossed.Select(finding => finding.Fingerprint));
        return new AnalysisThresholdOutcome(crossed, [.. closed]);
    }
}
