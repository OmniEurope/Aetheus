// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Analysis;

public sealed class AnalysisGradeEngineTests
{
    private static readonly DateTime EvaluatedAt =
        new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Evaluate_CoverageAtSeventyFivePercent_IsGradeA()
    {
        var report = Report(
            AnalysisCategory.Coverage,
            Metric("coverage.line.percent", 75, "percent"));

        var result = AnalysisGradeEngine.Evaluate(report, Context(CoverageYaml), EvaluatedAt);
        var summary = AnalysisGradeEngine.Aggregate([result.SnapshotJson]);

        Assert.Equal(AnalysisGrade.A, result.Grade);
        Assert.Equal(AnalysisGrade.A, summary!.OverallGrade);
        Assert.Equal(AnalysisGradeCompleteness.Complete, summary.Completeness);
    }

    [Fact]
    public void Aggregate_UsesWorstRequiredDomainWithoutAWeightedAverage()
    {
        var coverage = AnalysisGradeEngine.Evaluate(
            Report(AnalysisCategory.Coverage, Metric("coverage.line.percent", 80, "percent")),
            Context(QualityYaml),
            EvaluatedAt);
        var duplication = AnalysisGradeEngine.Evaluate(
            Report(AnalysisCategory.Duplication, Metric("duplication.percentage", 4, "percent")),
            Context(QualityYaml),
            EvaluatedAt);
        var architecture = AnalysisGradeEngine.Evaluate(
            Report(AnalysisCategory.Architecture, Metric("architecture.cycles", 2, "cycles")),
            Context(QualityYaml),
            EvaluatedAt);

        var summary = AnalysisGradeEngine.Aggregate(
            [coverage.SnapshotJson, duplication.SnapshotJson, architecture.SnapshotJson]);

        Assert.Equal(AnalysisGrade.C, summary!.OverallGrade);
        Assert.Equal(AnalysisGradeDomain.Architecture, summary.LimitingDomain);
    }

    [Fact]
    public void R1_38_Aggregate_KeepsTheUnitOfTheObservedMeasure_WhateverTheOrderOfTheReports()
    {
        // The duplication report evaluates the coverage rule too, finds no coverage metric (category
        // mismatch) and so carries the measure with no value and no unit. Taking that first measure as
        // the template lost the percent unit of the coverage report, and the Gate showed "88.64" bare.
        var duplication = AnalysisGradeEngine.Evaluate(
            Report(AnalysisCategory.Duplication, Metric("duplication.percentage", 4, "percent")),
            Context(QualityYaml),
            EvaluatedAt);
        var coverage = AnalysisGradeEngine.Evaluate(
            Report(AnalysisCategory.Coverage, Metric("coverage.line.percent", 88.64, "percent")),
            Context(QualityYaml),
            EvaluatedAt);

        var summary = AnalysisGradeEngine.Aggregate([duplication.SnapshotJson, coverage.SnapshotJson]);

        var measure = summary!.Domains.SelectMany(domain => domain.Measures)
            .Single(item => item.Key == "grade.coverage");
        Assert.Equal(88.64, measure.ObservedValue);
        Assert.Equal("percent", measure.Unit);
    }

    [Fact]
    public void Aggregate_ArchitectureMetricsRemainCompleteWhenFindingsReportHasNoMetric()
    {
        var metrics = AnalysisGradeEngine.Evaluate(
            Report(AnalysisCategory.Architecture, Metric("architecture.cycles", 0, "cycles")),
            Context(QualityYaml),
            EvaluatedAt);
        var findings = AnalysisGradeEngine.Evaluate(
            new AnalysisReport
            {
                Id = 5,
                Category = AnalysisCategory.Architecture,
                Status = AnalysisReportStatus.Passed
            },
            Context(QualityYaml),
            EvaluatedAt.AddSeconds(1));

        var summary = AnalysisGradeEngine.Aggregate(
            [metrics.SnapshotJson, findings.SnapshotJson]);
        var architecture = Assert.Single(summary!.Domains, domain =>
            domain.Domain == AnalysisGradeDomain.Architecture);

        Assert.Equal(AnalysisGrade.A, architecture.Grade);
        Assert.Equal(AnalysisGradeCompleteness.Complete, architecture.Completeness);
    }

    [Fact]
    public void Evaluate_CriticalFinding_ForcesGradeF()
    {
        var finding = new AnalysisFinding
        {
            Fingerprint = "critical-fingerprint",
            Severity = AnalysisSeverity.Critical,
            Category = AnalysisCategory.Sast
        };
        var report = new AnalysisReport
        {
            Id = 4,
            Category = AnalysisCategory.Sast,
            Status = AnalysisReportStatus.Passed,
            Occurrences =
            {
                new AnalysisFindingOccurrence
                {
                    IsNew = true,
                    AnalysisFinding = finding
                }
            }
        };

        var result = AnalysisGradeEngine.Evaluate(report, Context(SecurityYaml), EvaluatedAt);

        Assert.Equal(AnalysisGrade.F, result.Grade);
    }

    // A = no open finding, F = one critical, between them the weighted score of the open findings
    // (info 0.5, low 1, medium 3, high 10) against B <= 5, C <= 15, D <= 30, E above.
    [Theory]
    [InlineData(0, 0, 0, 0, 0, AnalysisGrade.A)]
    [InlineData(0, 0, 0, 1, 0, AnalysisGrade.B)]
    [InlineData(0, 0, 0, 0, 1, AnalysisGrade.B)]
    [InlineData(0, 0, 0, 5, 0, AnalysisGrade.B)]
    [InlineData(0, 0, 0, 6, 0, AnalysisGrade.C)]
    [InlineData(0, 1, 0, 0, 0, AnalysisGrade.C)]
    [InlineData(0, 1, 1, 2, 0, AnalysisGrade.C)]
    [InlineData(0, 1, 2, 0, 0, AnalysisGrade.D)]
    [InlineData(0, 3, 0, 0, 0, AnalysisGrade.D)]
    [InlineData(0, 4, 0, 0, 0, AnalysisGrade.E)]
    [InlineData(0, 40, 0, 0, 0, AnalysisGrade.E)]
    [InlineData(1, 0, 0, 0, 0, AnalysisGrade.F)]
    public void Evaluate_WeightedFindingScore_FollowsTheDecidedScale(
        int critical, int high, int medium, int low, int info, AnalysisGrade expected)
    {
        var report = FindingsReport(
            Enumerable.Repeat(AnalysisSeverity.Critical, critical)
                .Concat(Enumerable.Repeat(AnalysisSeverity.High, high))
                .Concat(Enumerable.Repeat(AnalysisSeverity.Medium, medium))
                .Concat(Enumerable.Repeat(AnalysisSeverity.Low, low))
                .Concat(Enumerable.Repeat(AnalysisSeverity.Info, info))
                .Select(severity => (severity, AnalysisFindingStatus.Open))
                .ToArray());

        var result = AnalysisGradeEngine.Evaluate(report, Context(WeightedYaml), EvaluatedAt);
        var measure = WeightedMeasure(result);

        Assert.Equal(expected, result.Grade);
        Assert.Equal(expected, measure.Grade);
        Assert.True(measure.WeightedFindingScore);
        Assert.Equal(high * 10 + medium * 3 + low + info * 0.5, measure.ObservedValue);
        Assert.Equal(
            new AnalysisGradeFindingCountsDto { Critical = critical, High = high, Medium = medium, Low = low, Info = info },
            measure.FindingCounts);
    }

    [Fact]
    public void Evaluate_WeightedFindingScore_IgnoresDecidedFindings()
    {
        // An accepted high, a false-positive critical and a mitigated medium are decided: only the one open
        // low finding counts, so the grade is B, not F.
        var report = FindingsReport(
            (AnalysisSeverity.High, AnalysisFindingStatus.Accepted),
            (AnalysisSeverity.Critical, AnalysisFindingStatus.FalsePositive),
            (AnalysisSeverity.Medium, AnalysisFindingStatus.Mitigated),
            (AnalysisSeverity.Low, AnalysisFindingStatus.Open));

        var result = AnalysisGradeEngine.Evaluate(report, Context(WeightedYaml), EvaluatedAt);

        Assert.Equal(AnalysisGrade.B, result.Grade);
        Assert.Equal(1, WeightedMeasure(result).ObservedValue);
    }

    [Fact]
    public void Evaluate_SeverityCount_IgnoresDecidedFindings()
    {
        // The critical rule counts open findings only: an accepted critical no longer forces F.
        var report = FindingsReport((AnalysisSeverity.Critical, AnalysisFindingStatus.Accepted));

        var result = AnalysisGradeEngine.Evaluate(report, Context(SecurityYaml), EvaluatedAt);

        Assert.Equal(AnalysisGrade.A, result.Grade);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, AnalysisGrade.A)]
    [InlineData(0, 1, 0, 0, 0, AnalysisGrade.B)]
    [InlineData(0, 0, 1, 6, 0, AnalysisGrade.C)]
    [InlineData(0, 0, 0, 5, 0, AnalysisGrade.B)]
    [InlineData(0, 0, 0, 16, 0, AnalysisGrade.D)]
    [InlineData(0, 0, 0, 0, 31, AnalysisGrade.E)]
    [InlineData(1, 0, 0, 0, 0, AnalysisGrade.F)]
    public void Evaluate_CountedFindingScore_GradesTheNumberOfOpenFindings(
        int critical, int high, int medium, int low, int info, AnalysisGrade expected)
    {
        // No weighting, every open finding counts one; a critical one still gives F.
        var report = FindingsReport(
            Enumerable.Repeat(AnalysisSeverity.Critical, critical)
                .Concat(Enumerable.Repeat(AnalysisSeverity.High, high))
                .Concat(Enumerable.Repeat(AnalysisSeverity.Medium, medium))
                .Concat(Enumerable.Repeat(AnalysisSeverity.Low, low))
                .Concat(Enumerable.Repeat(AnalysisSeverity.Info, info))
                .Select(severity => (severity, AnalysisFindingStatus.Open))
                .ToArray());

        var result = AnalysisGradeEngine.Evaluate(report, Context(CountedYaml), EvaluatedAt);
        var measure = WeightedMeasure(result);

        Assert.Equal(expected, measure.Grade);
        Assert.True(measure.CountedFindingScore);
        Assert.False(measure.WeightedFindingScore);
        Assert.Equal(critical + high + medium + low + info, measure.ObservedValue);
    }

    [Fact]
    public void Aggregate_CountedFindingScore_AddsTheNumbersOfTheRunReports()
    {
        // Run 2653: 1 medium and 6 low findings over two reports grade 7, not the weighted 9.
        var first = AnalysisGradeEngine.Evaluate(
            FindingsReport("a", [(AnalysisSeverity.Medium, AnalysisFindingStatus.Open)]),
            Context(CountedYaml), EvaluatedAt);
        var second = AnalysisGradeEngine.Evaluate(
            FindingsReport("b", Enumerable.Repeat((AnalysisSeverity.Low, AnalysisFindingStatus.Open), 6).ToArray()),
            Context(CountedYaml), EvaluatedAt);

        var summary = AnalysisGradeEngine.Aggregate([first.SnapshotJson, second.SnapshotJson]);
        var measure = summary!.Domains.Single(domain => domain.Domain == AnalysisGradeDomain.Security).Measures.Single();

        Assert.Equal(7, measure.ObservedValue);
        Assert.Equal(AnalysisGrade.C, measure.Grade);
    }

    [Fact]
    public void Aggregate_WeightedFindingScore_AddsTheCountsOfTheRunReports()
    {
        // Two reports of one run (two scanners): 3 low + 3 low = 6 low, score 6, grade C.
        var first = AnalysisGradeEngine.Evaluate(
            FindingsReport("a", Enumerable.Repeat((AnalysisSeverity.Low, AnalysisFindingStatus.Open), 3).ToArray()),
            Context(WeightedYaml), EvaluatedAt);
        var second = AnalysisGradeEngine.Evaluate(
            FindingsReport("b", Enumerable.Repeat((AnalysisSeverity.Low, AnalysisFindingStatus.Open), 3).ToArray()),
            Context(WeightedYaml), EvaluatedAt);

        var summary = AnalysisGradeEngine.Aggregate([first.SnapshotJson, second.SnapshotJson]);
        var measure = summary!.Domains.Single(domain => domain.Domain == AnalysisGradeDomain.Security).Measures.Single();

        Assert.Equal(AnalysisGrade.C, summary.OverallGrade);
        Assert.Equal(6, measure.ObservedValue);
        Assert.Equal(6, measure.FindingCounts!.Low);
    }

    [Fact]
    public void MergeRuns_KeepsTheSecurityDomainOfTheSecurityRun_WhenTheQualityRunDoesNotGradeIt()
    {
        var quality = AnalysisGradeEngine.Aggregate([AnalysisGradeEngine.Evaluate(
            Report(AnalysisCategory.Coverage, Metric("coverage.line.percent", 80, "percent")),
            Context(CoverageYaml), EvaluatedAt).SnapshotJson])!;
        var security = AnalysisGradeEngine.Aggregate([AnalysisGradeEngine.Evaluate(
            FindingsReport((AnalysisSeverity.High, AnalysisFindingStatus.Open)),
            Context(WeightedYaml), EvaluatedAt).SnapshotJson])!;

        var merged = AnalysisGradeEngine.MergeRuns([quality, security], 7);

        var securityDomain = merged.Domains.Single(domain => domain.Domain == AnalysisGradeDomain.Security);
        Assert.Equal(AnalysisGrade.C, securityDomain.Grade);
        Assert.True(securityDomain.Required);
        Assert.Equal(AnalysisGradeCompleteness.Complete, securityDomain.Completeness);
        Assert.Equal(AnalysisGrade.C, merged.OverallGrade);
        Assert.Equal(AnalysisGradeDomain.Security, merged.LimitingDomain);
        Assert.Equal(7, merged.PipelineRunId);
    }

    [Fact]
    public void MergeRuns_KeepsTheWorstGradeOfADomainTwoRunsMeasured_WithoutAddingTheirCounts()
    {
        // The security run and the QA run report the same high finding: 1 high (C) each, merged C, not
        // 2 highs (D).
        var security = AnalysisGradeEngine.Aggregate([AnalysisGradeEngine.Evaluate(
            FindingsReport((AnalysisSeverity.High, AnalysisFindingStatus.Open)),
            Context(WeightedYaml), EvaluatedAt).SnapshotJson])!;
        var qa = AnalysisGradeEngine.Aggregate([AnalysisGradeEngine.Evaluate(
            FindingsReport((AnalysisSeverity.High, AnalysisFindingStatus.Open)),
            Context(WeightedYaml), EvaluatedAt).SnapshotJson])!;
        var clean = AnalysisGradeEngine.Aggregate([AnalysisGradeEngine.Evaluate(
            FindingsReport(), Context(WeightedYaml), EvaluatedAt).SnapshotJson])!;

        var merged = AnalysisGradeEngine.MergeRuns([clean, security, qa], 9);
        var measure = merged.Domains.Single(domain => domain.Domain == AnalysisGradeDomain.Security).Measures.Single();

        Assert.Equal(AnalysisGrade.C, merged.OverallGrade);
        Assert.Equal(1, measure.FindingCounts!.High);
    }

    [Fact]
    public void Aggregate_DoesNotTurnMissingRequiredEvidenceIntoGradeF()
    {
        var report = Report(
            AnalysisCategory.Duplication,
            Metric("duplication.percentage", 2, "percent"));

        var result = AnalysisGradeEngine.Evaluate(report, Context(QualityYaml), EvaluatedAt);
        var summary = AnalysisGradeEngine.Aggregate([result.SnapshotJson]);

        Assert.Null(summary!.OverallGrade);
        Assert.Equal(AnalysisGradeCompleteness.Incomplete, summary.Completeness);
    }

    [Fact]
    public void Evaluate_MissingApplicableRequiredMeasure_IsIncompleteWithoutGrade()
    {
        var report = Report(
            AnalysisCategory.Coverage,
            Metric("coverage.line.percent", 80, "percent"));

        var result = AnalysisGradeEngine.Evaluate(
            report,
            Context(CoverageWithMissingRequiredMetricYaml),
            EvaluatedAt);

        Assert.Null(result.Grade);
        Assert.Equal(AnalysisGradeCompleteness.Incomplete, result.Completeness);
    }

    [Fact]
    public void Evaluate_ProducesDeterministicSnapshotHash()
    {
        var report = Report(
            AnalysisCategory.Coverage,
            Metric("coverage.line.percent", 75, "percent"));

        var first = AnalysisGradeEngine.Evaluate(report, Context(CoverageYaml), EvaluatedAt);
        var second = AnalysisGradeEngine.Evaluate(report, Context(CoverageYaml), EvaluatedAt);

        Assert.Equal(first.SnapshotJson, second.SnapshotJson);
        Assert.Equal(first.SnapshotHash, second.SnapshotHash);
        Assert.Equal(64, first.SnapshotHash.Length);
    }

    private static AnalysisReport Report(AnalysisCategory category, AnalysisMetric metric) => new()
    {
        Id = 4,
        Category = category,
        Status = AnalysisReportStatus.Passed,
        Metrics = { metric }
    };

    private static AnalysisReport FindingsReport(params (AnalysisSeverity Severity, AnalysisFindingStatus Status)[] findings) =>
        FindingsReport("finding", findings);

    private static AnalysisReport FindingsReport(
        string prefix,
        params (AnalysisSeverity Severity, AnalysisFindingStatus Status)[] findings)
    {
        var report = new AnalysisReport { Id = 4, Category = AnalysisCategory.Sast, Status = AnalysisReportStatus.Passed };
        for (var index = 0; index < findings.Length; index++)
        {
            report.Occurrences.Add(new AnalysisFindingOccurrence
            {
                IsNew = true,
                AnalysisFinding = new AnalysisFinding
                {
                    Fingerprint = $"{prefix}-{index}",
                    Severity = findings[index].Severity,
                    Status = findings[index].Status,
                    Category = AnalysisCategory.Sast
                }
            });
        }
        return report;
    }

    private static AnalysisGradeMeasureDto WeightedMeasure(AnalysisGradeEvaluation result) =>
        AnalysisGradeEngine.Aggregate([result.SnapshotJson])!.Domains
            .Single(domain => domain.Domain == AnalysisGradeDomain.Security)
            .Measures.Single(measure => measure.Key == "grade.security.findings");

    private static AnalysisMetric Metric(string key, double value, string unit) => new()
    {
        Key = key,
        Value = value,
        Unit = unit,
        ToolName = "test"
    };

    private static AnalysisRunContext Context(string yaml) =>
        new(1, 2, 3, "main", new string('a', 40), "main", PipelineYaml: yaml);

    private const string QualityYaml = """
        stages:
          - name: Quality
            steps:
              - name: Grade
                type: analysis-gate
                analysis_scope: quality
                analysis_grading:
                  version: 1
                  minimum_grade: C
                  required_domains: [reliability, code-quality, architecture]
                  rules:
                    - key: grade.coverage
                      domain: reliability
                      metric: coverage.line.percent
                      category: coverage
                      direction: higher-is-better
                      a: 75
                      b: 65
                      c: 55
                      d: 40
                      e: 20
                    - key: grade.duplication
                      domain: code-quality
                      metric: duplication.percentage
                      category: duplication
                      direction: lower-is-better
                      a: 3
                      b: 5
                      c: 8
                      d: 12
                      e: 20
                    - key: grade.architecture
                      domain: architecture
                      metric: architecture.cycles
                      category: architecture
                      direction: lower-is-better
                      a: 0
                      b: 1
                      c: 2
                      d: 4
                      e: 8
        """;

    private const string CoverageYaml = """
        stages:
          - name: Quality
            steps:
              - name: Grade
                type: analysis-gate
                analysis_scope: quality
                analysis_grading:
                  version: 1
                  minimum_grade: C
                  required_domains: [reliability]
                  rules:
                    - key: grade.coverage
                      domain: reliability
                      metric: coverage.line.percent
                      category: coverage
                      direction: higher-is-better
                      a: 75
                      b: 65
                      c: 55
                      d: 40
                      e: 20
        """;

    private const string CoverageWithMissingRequiredMetricYaml = """
        stages:
          - name: Quality
            steps:
              - name: Grade
                type: analysis-gate
                analysis_scope: quality
                analysis_grading:
                  version: 1
                  minimum_grade: C
                  required_domains: [reliability]
                  rules:
                    - key: grade.coverage.line
                      domain: reliability
                      metric: coverage.line.percent
                      category: coverage
                      direction: higher-is-better
                      a: 75
                      b: 65
                      c: 55
                      d: 40
                      e: 20
                    - key: grade.coverage.branch
                      domain: reliability
                      metric: coverage.branch.percent
                      category: coverage
                      direction: higher-is-better
                      a: 75
                      b: 65
                      c: 55
                      d: 40
                      e: 20
        """;

    private const string WeightedYaml = """
        stages:
          - name: Security
            steps:
              - name: Grade
                type: analysis-gate
                analysis_scope: security
                analysis_grading:
                  version: 1
                  minimum_grade: F
                  required_domains: [security]
                  rules:
                    - key: grade.security.findings
                      domain: security
                      finding_score: weighted
                      direction: lower-is-better
                      a: 0
                      b: 5
                      c: 15
                      d: 30
        """;

    private const string CountedYaml = """
        stages:
          - name: Security
            steps:
              - name: Grade
                type: analysis-gate
                analysis_scope: security
                analysis_grading:
                  version: 1
                  minimum_grade: F
                  required_domains: [security]
                  rules:
                    - key: grade.security.findings
                      domain: security
                      finding_score: count
                      direction: lower-is-better
                      a: 0
                      b: 5
                      c: 15
                      d: 30
        """;

    private const string SecurityYaml = """
        stages:
          - name: Security
            steps:
              - name: Grade
                type: analysis-gate
                analysis_scope: security
                analysis_grading:
                  version: 1
                  minimum_grade: C
                  required_domains: [security]
                  rules:
                    - key: grade.security.critical
                      domain: security
                      severity: critical
                      direction: lower-is-better
                      a: 0
                      b: 0
                      c: 0
                      d: 0
                      e: 0
        """;
}
