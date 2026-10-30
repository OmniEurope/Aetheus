// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>Recette R-73 and R-74, decided on 2026-10-25: a domain always has a grade whatever the findings, no
/// finding is an A, the first finding is a B at best, and the grade then falls with the severity (medium, high,
/// critical). The scale is written here as inline YAML (the same rules the generic templates carry); no test reads a
/// pipeline definition file (STD-TESTSCOPE).</summary>
public sealed class AnalysisGradeAlwaysGradedTests
{
    private static readonly DateTime EvaluatedAt = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    // The owner's scale of 2026-10-25 (R-73, option 1), findings never produce an E: no finding A; medium, low and
    // info 1 to 5 B, 6 or more C, never worse (a rule that stops its thresholds leaves the next grade open);
    // one high or more D, whatever the count; one critical or more F.
    private const string SecurityScaleYaml = """
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
                    - key: grade.security.critical
                      domain: security
                      severity: critical
                      direction: lower-is-better
                      a: 0
                      b: 0
                      c: 0
                      d: 0
                      e: 0
                    - key: grade.security.high
                      domain: security
                      severity: high
                      direction: lower-is-better
                      a: 0
                      b: 0
                      c: 0
                    - key: grade.security.medium
                      domain: security
                      severity: medium
                      direction: lower-is-better
                      a: 0
                      b: 5
                    - key: grade.security.low
                      domain: security
                      severity: low
                      direction: lower-is-better
                      a: 0
                      b: 5
                    - key: grade.security.info
                      domain: security
                      severity: info
                      direction: lower-is-better
                      a: 0
                      b: 5
        """;

    private const string QualityScaleYaml = """
        stages:
          - name: Publish coverage
            steps:
              - name: Publish coverage evidence
                type: coverage
                target_files: [".pipeline-quality/coverage.cobertura.xml"]
          - name: Measure complexity
            depends_on: [Publish coverage]
            steps:
              - name: Publish complexity and architecture evidence
                type: complexity
          - name: Grade quality
            depends_on: [Measure complexity]
            steps:
              - name: Grade
                type: analysis-gate
                analysis_scope: quality
                analysis_grading:
                  version: 1
                  minimum_grade: F
                  required_domains: [reliability, code-quality, architecture]
                  rules:
                    - key: grade.quality.high
                      domain: code-quality
                      severity: high
                      direction: lower-is-better
                      a: 0
                      b: 1
                      c: 3
                      d: 5
                      e: 10
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
                    - key: grade.architecture.critical
                      domain: architecture
                      severity: critical
                      category: architecture
                      direction: lower-is-better
                      a: 0
                      b: 0
                      c: 0
                      d: 0
                      e: 0
                    - key: grade.architecture.high
                      domain: architecture
                      severity: high
                      category: architecture
                      direction: lower-is-better
                      a: 0
                      b: 0
                      c: 0
                    - key: grade.architecture.medium
                      domain: architecture
                      severity: medium
                      category: architecture
                      direction: lower-is-better
                      a: 0
                      b: 5
                    - key: grade.architecture.low
                      domain: architecture
                      severity: low
                      category: architecture
                      direction: lower-is-better
                      a: 0
                      b: 5
                    - key: grade.architecture.info
                      domain: architecture
                      severity: info
                      category: architecture
                      direction: lower-is-better
                      a: 0
                      b: 5
        """;

    // critical, high, medium, low, info
    [Theory]
    [InlineData(0, 0, 0, 0, 0, AnalysisGrade.A)]
    [InlineData(0, 0, 1, 0, 0, AnalysisGrade.B)]
    [InlineData(0, 0, 5, 0, 0, AnalysisGrade.B)]
    [InlineData(0, 0, 6, 0, 0, AnalysisGrade.C)]
    [InlineData(0, 0, 100, 0, 0, AnalysisGrade.C)]
    [InlineData(0, 0, 0, 1, 0, AnalysisGrade.B)]
    [InlineData(0, 0, 0, 0, 1, AnalysisGrade.B)]
    [InlineData(0, 0, 0, 6, 0, AnalysisGrade.C)]
    [InlineData(0, 0, 0, 0, 100, AnalysisGrade.C)]
    [InlineData(0, 1, 0, 0, 0, AnalysisGrade.D)]
    [InlineData(0, 3, 0, 0, 0, AnalysisGrade.D)]
    [InlineData(0, 10, 0, 0, 0, AnalysisGrade.D)]
    [InlineData(0, 100, 0, 0, 0, AnalysisGrade.D)]
    [InlineData(0, 1, 50, 0, 0, AnalysisGrade.D)]
    [InlineData(0, 4, 100, 100, 100, AnalysisGrade.D)]
    [InlineData(1, 0, 0, 0, 0, AnalysisGrade.F)]
    [InlineData(1, 100, 100, 100, 100, AnalysisGrade.F)]
    public void TheSecurityScale_GradesEveryCombination(
        int critical, int high, int medium, int low, int info, AnalysisGrade expected)
    {
        var report = ReportOf(AnalysisCategory.Sast, critical, high, medium, low, info);

        var result = AnalysisGradeEngine.Evaluate(report, Context(SecurityScaleYaml), EvaluatedAt);
        var domain = DomainOf(result, AnalysisGradeDomain.Security);

        Assert.Equal(expected, result.Grade);
        Assert.Equal(expected, domain.Grade);
        Assert.Equal(AnalysisGradeCompleteness.Complete, domain.Completeness);
    }

    [Fact]
    public void ADomainWithNoFinding_IsAGradeA_NotIncomplete()
    {
        var result = AnalysisGradeEngine.Evaluate(ReportOf(AnalysisCategory.Sast), Context(SecurityScaleYaml), EvaluatedAt);
        var summary = AnalysisGradeEngine.Aggregate([result.SnapshotJson])!;

        var security = summary.Domains.Single(domain => domain.Domain == AnalysisGradeDomain.Security);
        Assert.Equal(AnalysisGrade.A, security.Grade);
        Assert.Equal(AnalysisGradeCompleteness.Complete, security.Completeness);
        Assert.Equal(AnalysisGrade.A, summary.OverallGrade);
        Assert.Equal(AnalysisGradeCompleteness.Complete, summary.Completeness);
        Assert.All(security.Measures, measure => Assert.True(measure.Required));
    }

    [Fact]
    public void TheMediumSecurityRule_CountsInTheGrade_BecauseItIsRequired()
    {
        // Run #2920: two medium findings, an A beside a B line. The medium rule is required, so the grade is the B.
        var result = AnalysisGradeEngine.Evaluate(
            ReportOf(AnalysisCategory.Sast, medium: 2), Context(SecurityScaleYaml), EvaluatedAt);
        var summary = AnalysisGradeEngine.Aggregate([result.SnapshotJson])!;

        var medium = summary.Domains.Single(domain => domain.Domain == AnalysisGradeDomain.Security)
            .Measures.Single(measure => measure.Key == "grade.security.medium");
        Assert.True(medium.Required);
        Assert.Equal(AnalysisGrade.B, medium.Grade);
        Assert.Equal(AnalysisGrade.B, summary.OverallGrade);
    }

    [Fact]
    public void ForTheSameNumberOfFindings_ASeverityIsNeverGradedBetterThanALessSevereOne()
    {
        // medium < high < critical, at every count: the order the owner asked for on 2026-10-25.
        for (var count = 1; count <= 70; count++)
        {
            var medium = AnalysisGradeEngine.Evaluate(
                ReportOf(AnalysisCategory.Sast, medium: count), Context(SecurityScaleYaml), EvaluatedAt).Grade!.Value;
            var high = AnalysisGradeEngine.Evaluate(
                ReportOf(AnalysisCategory.Sast, high: count), Context(SecurityScaleYaml), EvaluatedAt).Grade!.Value;
            var critical = AnalysisGradeEngine.Evaluate(
                ReportOf(AnalysisCategory.Sast, critical: count), Context(SecurityScaleYaml), EvaluatedAt).Grade!.Value;

            Assert.True(high >= medium, $"{count} high ({high}) graded better than {count} medium ({medium})");
            Assert.True(critical >= high, $"{count} critical ({critical}) graded better than {count} high ({high})");
            Assert.True(medium > AnalysisGrade.A, $"{count} medium findings are still an A");
        }
    }

    [Theory]
    [InlineData(0, 0, 0, 0, AnalysisGrade.A)]
    [InlineData(0, 0, 1, 0, AnalysisGrade.B)]
    [InlineData(0, 0, 3, 0, AnalysisGrade.B)]
    [InlineData(0, 0, 6, 0, AnalysisGrade.C)]
    [InlineData(0, 0, 0, 1, AnalysisGrade.B)]
    [InlineData(0, 1, 0, 0, AnalysisGrade.D)]
    [InlineData(0, 2, 0, 0, AnalysisGrade.D)]
    [InlineData(0, 10, 0, 0, AnalysisGrade.D)]
    [InlineData(0, 0, 100, 0, AnalysisGrade.C)]
    [InlineData(1, 0, 0, 0, AnalysisGrade.F)]
    public void TheArchitectureDomain_FollowsTheSameScale(int critical, int high, int medium, int low, AnalysisGrade expected)
    {
        // Run #2920: three medium architecture findings and a domain no pipeline graded. Now it is graded.
        var result = AnalysisGradeEngine.Evaluate(
            ReportOf(AnalysisCategory.Architecture, critical, high, medium, low), Context(QualityScaleYaml), EvaluatedAt);
        var architecture = DomainOf(result, AnalysisGradeDomain.Architecture);

        Assert.Equal(expected, architecture.Grade);
        Assert.True(architecture.Required);
        Assert.Equal(AnalysisGradeCompleteness.Complete, architecture.Completeness);
    }

    [Fact]
    public void AnArchitectureReportWithoutFindings_IsAnA_NotIncomplete()
    {
        var result = AnalysisGradeEngine.Evaluate(
            ReportOf(AnalysisCategory.Architecture), Context(QualityScaleYaml), EvaluatedAt);

        var architecture = DomainOf(result, AnalysisGradeDomain.Architecture);

        Assert.Equal(AnalysisGrade.A, architecture.Grade);
        Assert.Equal(AnalysisGradeCompleteness.Complete, architecture.Completeness);
    }

    [Fact]
    public void TheArchitectureRules_OnlyCountTheFindingsOfTheArchitectureReports()
    {
        // A code-quality report holding a high finding raises no architecture finding: the architecture rules are
        // bound to their category, only the quality rule without category sees that report.
        var quality = AnalysisGradeEngine.Evaluate(
            ReportOf(AnalysisCategory.CodeQuality, high: 1, id: 5), Context(QualityScaleYaml), EvaluatedAt);
        var architecture = AnalysisGradeEngine.Evaluate(
            ReportOf(AnalysisCategory.Architecture, medium: 3, id: 6), Context(QualityScaleYaml), EvaluatedAt);

        var summary = AnalysisGradeEngine.Aggregate([quality.SnapshotJson, architecture.SnapshotJson])!;

        var domain = summary.Domains.Single(item => item.Domain == AnalysisGradeDomain.Architecture);
        Assert.Equal(AnalysisGrade.B, domain.Grade);
        Assert.Equal(3, domain.Measures.Single(measure => measure.Key == "grade.architecture.medium").ObservedValue);
        Assert.Equal(0, domain.Measures.Single(measure => measure.Key == "grade.architecture.high").ObservedValue);
        var codeQuality = summary.Domains.Single(item => item.Domain == AnalysisGradeDomain.CodeQuality);
        Assert.Equal(AnalysisGrade.B, codeQuality.Grade);
    }

    [Fact]
    public void ARequiredMeasureNobodyObserved_IsNotAnA_ItIsMissingAndNamesItsStep()
    {
        // Only the architecture report was published: the coverage step produced nothing. The reliability domain is
        // not an A and not silent: its required coverage measure is unobserved and says which step publishes it.
        var architecture = AnalysisGradeEngine.Evaluate(
            ReportOf(AnalysisCategory.Architecture), Context(QualityScaleYaml), EvaluatedAt);

        var summary = AnalysisGradeEngine.Aggregate([architecture.SnapshotJson])!;

        var reliability = summary.Domains.Single(domain => domain.Domain == AnalysisGradeDomain.Reliability);
        Assert.Null(reliability.Grade);
        var coverage = Assert.Single(reliability.Measures);
        Assert.True(coverage.Required);
        Assert.False(coverage.Observed);
        Assert.Null(coverage.Grade);
        Assert.Equal("Publish coverage evidence", coverage.ProducerStep);
        Assert.Equal(AnalysisGradeCompleteness.Incomplete, reliability.Completeness);
    }

    [Fact]
    public void WhenARequiredMeasureIsMissing_TheHeadlineGradeIsWhatWasObserved_FlaggedAsNotComplete()
    {
        // The coverage step produced nothing: the grade is still there, from the architecture and quality evidence,
        // and the summary says that it does not rest on every required measure (the sealed candidate contract reads
        // that flag and holds such a grade back as unavailable).
        var architecture = AnalysisGradeEngine.Evaluate(
            ReportOf(AnalysisCategory.Architecture, medium: 2), Context(QualityScaleYaml), EvaluatedAt);

        var summary = AnalysisGradeEngine.Aggregate([architecture.SnapshotJson])!;

        Assert.Equal(AnalysisGrade.B, summary.OverallGrade);
        Assert.Equal(AnalysisGradeDomain.Architecture, summary.LimitingDomain);
        Assert.Equal(AnalysisGradeCompleteness.Incomplete, summary.Completeness);
    }

    [Fact]
    public void WhenNoRequiredMeasureWasObservedAtAll_NoLetterIsInvented()
    {
        var coverageOnly = AnalysisGradeEngine.Evaluate(
            new AnalysisReport { Id = 7, Category = AnalysisCategory.Coverage, Status = AnalysisReportStatus.Passed },
            Context(CoverageOnlyYaml), EvaluatedAt);

        var summary = AnalysisGradeEngine.Aggregate([coverageOnly.SnapshotJson])!;

        Assert.Null(coverageOnly.Grade);
        Assert.Null(summary.OverallGrade);
        Assert.Null(summary.LimitingDomain);
        Assert.Equal(AnalysisGradeCompleteness.Incomplete, summary.Completeness);
    }

    [Fact]
    public void AReportInError_ObservesNothing_SoItsGradeIsNeverAnA()
    {
        var failed = ReportOf(AnalysisCategory.Sast);
        failed.Status = AnalysisReportStatus.Error;

        var result = AnalysisGradeEngine.Evaluate(failed, Context(SecurityScaleYaml), EvaluatedAt);

        Assert.Null(result.Grade);
        Assert.All(DomainOf(result, AnalysisGradeDomain.Security).Measures, measure => Assert.False(measure.Observed));
    }

    [Fact]
    public void ADomainTheGradeHasNoRuleFor_HasNoGradeAndNoMeasure()
    {
        // The security scale has no architecture rule: that domain is not graded, it is simply absent of rules.
        var result = AnalysisGradeEngine.Evaluate(ReportOf(AnalysisCategory.Sast, medium: 2), Context(SecurityScaleYaml), EvaluatedAt);

        var architecture = DomainOf(result, AnalysisGradeDomain.Architecture);

        Assert.Null(architecture.Grade);
        Assert.Empty(architecture.Measures);
        Assert.False(architecture.Required);
    }

    [Fact]
    public void MergeRuns_KeepsTheGradeOfADomainMeasuredOnlyInPart()
    {
        var architectureOnly = AnalysisGradeEngine.Aggregate([AnalysisGradeEngine.Evaluate(
            ReportOf(AnalysisCategory.Architecture, medium: 3), Context(QualityScaleYaml), EvaluatedAt).SnapshotJson])!;
        var security = AnalysisGradeEngine.Aggregate([AnalysisGradeEngine.Evaluate(
            ReportOf(AnalysisCategory.Sast, high: 1), Context(SecurityScaleYaml), EvaluatedAt).SnapshotJson])!;

        var merged = AnalysisGradeEngine.MergeRuns([architectureOnly, security], 11);

        Assert.Equal(AnalysisGrade.D, merged.OverallGrade);
        Assert.Equal(AnalysisGradeDomain.Security, merged.LimitingDomain);
        Assert.Equal(AnalysisGrade.B, merged.Domains.Single(domain => domain.Domain == AnalysisGradeDomain.Architecture).Grade);
        // The coverage the quality run never measured stays visible as missing on the merged grade.
        Assert.Contains(merged.Domains.SelectMany(domain => domain.Measures),
            measure => measure is { Key: "grade.coverage.line", Required: true, Observed: false });
    }

    [Theory]
    [InlineData("grade.coverage.line", "Publish coverage evidence")]
    [InlineData("grade.architecture.high", "Publish complexity and architecture evidence")]
    [InlineData("grade.quality.high", null)]
    public void TheProducerStepOfAMeasure_IsTheStepThePipelinePublishesItsEvidenceWith(string key, string? expected)
    {
        var result = AnalysisGradeEngine.Evaluate(
            ReportOf(AnalysisCategory.CodeQuality), Context(QualityScaleYaml), EvaluatedAt);

        var measure = AnalysisGradeEngine.Aggregate([result.SnapshotJson])!.Domains
            .SelectMany(domain => domain.Measures).Single(item => item.Key == key);

        Assert.Equal(expected, measure.ProducerStep);
    }

    [Theory]
    [InlineData("grade.complexity.maximum", "Publish complexity evidence")]
    [InlineData("grade.lint.high", "Publish lint evidence, Publish complexity evidence, Lint Python")]
    public void AMetricIsNeverAttributedToALintStep_AFindingCountIs(string key, string expected)
    {
        var result = AnalysisGradeEngine.Evaluate(
            ReportOf(AnalysisCategory.Architecture), Context(LintAndComplexityYaml), EvaluatedAt);

        var measure = AnalysisGradeEngine.Aggregate([result.SnapshotJson])!.Domains
            .SelectMany(domain => domain.Measures).Single(item => item.Key == key);

        Assert.False(measure.Observed);
        Assert.Equal(expected, measure.ProducerStep);
    }

    private static AnalysisGradeDomainDto DomainOf(AnalysisGradeEvaluation result, AnalysisGradeDomain domain) =>
        AnalysisGradeEngine.Aggregate([result.SnapshotJson])!.Domains.Single(item => item.Domain == domain);

    private static AnalysisReport ReportOf(
        AnalysisCategory category, int critical = 0, int high = 0, int medium = 0, int low = 0, int info = 0, int id = 4)
    {
        var report = new AnalysisReport { Id = id, Category = category, Status = AnalysisReportStatus.Passed };
        var severities = Enumerable.Repeat(AnalysisSeverity.Critical, critical)
            .Concat(Enumerable.Repeat(AnalysisSeverity.High, high))
            .Concat(Enumerable.Repeat(AnalysisSeverity.Medium, medium))
            .Concat(Enumerable.Repeat(AnalysisSeverity.Low, low))
            .Concat(Enumerable.Repeat(AnalysisSeverity.Info, info))
            .ToList();
        for (var index = 0; index < severities.Count; index++)
        {
            report.Occurrences.Add(new AnalysisFindingOccurrence
            {
                IsNew = true,
                AnalysisFinding = new AnalysisFinding
                {
                    Fingerprint = $"{category}-{id}-{index}",
                    Severity = severities[index],
                    Status = AnalysisFindingStatus.Open,
                    Category = category
                }
            });
        }
        return report;
    }

    private static AnalysisRunContext Context(string yaml) =>
        new(1, 2, 3, "main", new string('a', 40), "main", PipelineYaml: yaml);

    private const string LintAndComplexityYaml = """
        stages:
          - name: Publish lint
            steps:
              - name: Publish lint evidence
                type: lint
                target_files: [".pipeline-quality/lint.sarif"]
          - name: Measure complexity
            depends_on: [Publish lint]
            steps:
              - name: Publish complexity evidence
                type: complexity
          - name: Lint source
            depends_on: [Measure complexity]
            steps:
              - name: Lint Python
                type: scanner
                scanner: ruff
          - name: Grade quality
            depends_on: [Lint source]
            steps:
              - name: Grade
                type: analysis-gate
                analysis_scope: quality
                analysis_grading:
                  version: 1
                  minimum_grade: F
                  required_domains: [code-quality]
                  rules:
                    - key: grade.complexity.maximum
                      domain: code-quality
                      metric: complexity.cyclomatic.maximum
                      category: code-quality
                      direction: lower-is-better
                      a: 15
                      b: 20
                      c: 25
                      d: 35
                      e: 50
                    - key: grade.lint.high
                      domain: code-quality
                      severity: high
                      category: code-quality
                      direction: lower-is-better
                      a: 0
                      b: 1
                      c: 3
                      d: 5
                      e: 10
        """;

    private const string CoverageOnlyYaml = """
        stages:
          - name: Publish coverage
            steps:
              - name: Publish coverage evidence
                type: coverage
                target_files: [".pipeline-quality/coverage.cobertura.xml"]
          - name: Grade quality
            depends_on: [Publish coverage]
            steps:
              - name: Grade
                type: analysis-gate
                analysis_scope: quality
                analysis_grading:
                  version: 1
                  minimum_grade: F
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
        """;
}
