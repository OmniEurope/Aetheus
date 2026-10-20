// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

internal static class AnalysisDefaultPolicyCatalog
{
    private static readonly DateTime CatalogDate = new(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);

    public static IReadOnlyList<AnalysisPolicy> Policies { get; } =
    [
        Finding(-1, "system.security.new-high.sast", AnalysisCategory.Sast, AnalysisSeverity.High, AnalysisGateBehavior.Warn),
        Finding(-2, "system.security.new-high.secrets", AnalysisCategory.Secrets, AnalysisSeverity.High, AnalysisGateBehavior.Warn),
        Finding(-3, "system.security.new-high.dependencies", AnalysisCategory.Dependencies, AnalysisSeverity.High, AnalysisGateBehavior.Warn),
        Finding(-4, "system.security.new-high.container", AnalysisCategory.Container, AnalysisSeverity.High, AnalysisGateBehavior.Warn),
        Finding(-5, "system.security.new-high.iac", AnalysisCategory.InfrastructureAsCode, AnalysisSeverity.High, AnalysisGateBehavior.Warn),
        Finding(-6, "system.security.new-high.dast", AnalysisCategory.Dast, AnalysisSeverity.High, AnalysisGateBehavior.Warn),
        Finding(-7, "system.quality.new-medium.code", AnalysisCategory.CodeQuality, AnalysisSeverity.Medium, AnalysisGateBehavior.Warn),
        Finding(-8, "system.quality.new-medium.coverage", AnalysisCategory.Coverage, AnalysisSeverity.Medium, AnalysisGateBehavior.Warn),
        Finding(-9, "system.quality.new-medium.duplication", AnalysisCategory.Duplication, AnalysisSeverity.Medium, AnalysisGateBehavior.Warn),
        Finding(-10, "system.quality.new-medium.architecture", AnalysisCategory.Architecture, AnalysisSeverity.Medium, AnalysisGateBehavior.Warn),
        // Serious and critical accessibility findings match the suite blocking threshold.
        Finding(-18, "system.quality.new-high.accessibility", AnalysisCategory.Accessibility, AnalysisSeverity.High, AnalysisGateBehavior.Warn),
        // A360-74, raised from 75 to 80 on the user's decision of 2026-08-21. The finding described this
        // as "min_coverage: 0 in the CI yaml", which was the wrong place twice over: that zero is
        // deliberate and guarded (a threshold is versioned here, never in pipeline YAML), and the real
        // default gate was this line, trailing at 75 while the product had been above 80 for weeks.
        //
        // It stays Warn, and that is not a half-measure: EVERY policy in this catalog warns, asserted by
        // AnalysisPolicyCatalogAuditTests. The catalog is the system-wide DEFAULT, and a default that
        // blocks would fail projects that never opted into it; blocking is a project-scoped policy, set
        // per project against this same metric key. Making the system default blocking is a separate
        // decision that would overturn that invariant, not a side effect of moving a number.
        Metric(-11, "quality.coverage.line", null, "coverage.line.percent",
            AnalysisPolicyOperator.LessThan, 80, AnalysisGateBehavior.Warn),
        Metric(-12, "quality.duplication.percentage", null, "duplication.percentage",
            AnalysisPolicyOperator.GreaterThan, 5),
        Metric(-13, "quality.complexity.average", null, "complexity.cyclomatic.average",
            AnalysisPolicyOperator.GreaterThan, 10),
        Metric(-14, "quality.complexity.maximum", null, "complexity.cyclomatic.maximum",
            AnalysisPolicyOperator.GreaterThan, 25),
        // A ratchet, not an absolute: the count of overly complex methods must never go up. It warns when
        // the count is above the one of the previous analysis (the latest successful run on the project's
        // default branch, the baseline every Degraded policy uses) and says both numbers. Equal or lower
        // is silent, and so is the first analysis, which has no previous one: its count becomes the
        // baseline. The "0" threshold is not read by Degraded. It names the metric, so it replaces the
        // catch-all degradation policy below for this metric (one alert, not two).
        Metric(-15, "quality.complexity.high-methods", null, "complexity.methods.high",
            AnalysisPolicyOperator.Degraded, 0),
        Metric(-16, "quality.architecture.cycles", null, "architecture.cycles*",
            AnalysisPolicyOperator.GreaterThan, 0),
        Metric(-17, "quality.metric.degradation", null, "*", AnalysisPolicyOperator.Degraded, 0)
    ];

    private static AnalysisPolicy Finding(
        int id,
        string key,
        AnalysisCategory category,
        AnalysisSeverity severity,
        AnalysisGateBehavior behavior)
    {
        var policy = Base(id, key, behavior);
        policy.Category = category;
        policy.SeverityThreshold = severity;
        policy.NewFindingsOnly = true;
        return policy;
    }

    private static AnalysisPolicy Metric(
        int id,
        string key,
        AnalysisCategory? category,
        string metricKey,
        AnalysisPolicyOperator policyOperator,
        double threshold,
        AnalysisGateBehavior behavior = AnalysisGateBehavior.Warn)
    {
        var policy = Base(id, key, behavior);
        policy.Category = category;
        policy.MetricKey = metricKey;
        policy.Operator = policyOperator;
        policy.Threshold = threshold;
        return policy;
    }

    private static AnalysisPolicy Base(int id, string key, AnalysisGateBehavior behavior) => new()
    {
        Id = id,
        PolicyKey = key,
        Name = key,
        Behavior = behavior,
        Priority = -10_000,
        Enabled = true,
        Version = 1,
        CreatedAt = CatalogDate,
        UpdatedAt = CatalogDate
    };
}
