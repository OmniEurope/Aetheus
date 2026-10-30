// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// Owner decision after run #2958 (8 valid reports, no finding, "note incomplète" with no reason): the valid rules of a
/// grading are always graded, each rule the validator rejects is an F that carries the validator's message, a grading
/// block that cannot be used at all is an overall F with its message, and nothing measured is no grade, never an error.
/// </summary>
public sealed class AnalysisGradeRejectedRuleTests
{
    private static readonly DateTime EvaluatedAt = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OneInvalidAndOneValidRule_GradesTheValidRule_AndTheInvalidOneIsAnFWithItsReason()
    {
        var report = CoverageReport(80);

        var result = AnalysisGradeEngine.Evaluate(report, Context(OneInvalidRuleYaml), EvaluatedAt);
        var summary = AnalysisGradeEngine.Aggregate([result.SnapshotJson]);

        Assert.Equal(AnalysisGrade.F, result.Grade);
        Assert.NotNull(summary);
        Assert.Equal(AnalysisGrade.F, summary.OverallGrade);
        Assert.Equal(new string('a', 40), summary.CommitHash);
        var measures = summary.Domains.SelectMany(domain => domain.Measures).ToList();
        var valid = Assert.Single(measures, measure => measure.Key == "grade.coverage");
        Assert.True(valid.Observed);
        Assert.Equal(AnalysisGrade.A, valid.Grade);
        Assert.Null(valid.Rejection);
        var rejected = Assert.Single(measures, measure => measure.Key == "grade.duplication");
        Assert.True(rejected.Observed);
        Assert.True(rejected.Required);
        Assert.Equal(AnalysisGrade.F, rejected.Grade);
        Assert.Equal(AnalysisGradeDomain.CodeQuality, rejected.Domain);
        Assert.Equal(
            "analysis grading rule 'grade.duplication' must define finite A, B, C, D and E thresholds.",
            rejected.Rejection);
    }

    [Fact]
    public void AGradingBlockThatCannotBeUsed_IsAnOverallFWithItsReason()
    {
        var result = AnalysisGradeEngine.Evaluate(CoverageReport(80), Context(WrongVersionYaml), EvaluatedAt);
        var summary = AnalysisGradeEngine.Aggregate([result.SnapshotJson]);

        Assert.Equal(AnalysisGrade.F, result.Grade);
        Assert.Equal(AnalysisGrade.F, summary!.OverallGrade);
        var rejected = Assert.Single(
            summary.Domains.SelectMany(domain => domain.Measures),
            measure => measure.Rejection is not null);
        Assert.Equal("analysis_grading", rejected.Key);
        Assert.Equal("analysis_grading version must be 1.", rejected.Rejection);
    }

    [Fact]
    public void AllRulesValidButNoneApplies_GivesNoGrade_IncompleteAndNoException()
    {
        // The report published no metric the rules read: nothing measured and no problem.
        var report = new AnalysisReport { Id = 4, Category = AnalysisCategory.Coverage, Status = AnalysisReportStatus.Passed };

        var result = AnalysisGradeEngine.Evaluate(report, Context(ValidYaml), EvaluatedAt);
        var summary = AnalysisGradeEngine.Aggregate([result.SnapshotJson]);

        Assert.Null(result.Grade);
        Assert.Equal(AnalysisGradeCompleteness.Incomplete, result.Completeness);
        Assert.NotNull(summary);
        Assert.Null(summary.OverallGrade);
        Assert.Equal(AnalysisGradeCompleteness.Incomplete, summary.Completeness);
        Assert.DoesNotContain(summary.Domains.SelectMany(domain => domain.Measures), measure => measure.Rejection is not null);
    }

    private static AnalysisReport CoverageReport(double percent) => new()
    {
        Id = 4,
        Category = AnalysisCategory.Coverage,
        Status = AnalysisReportStatus.Passed,
        Metrics = { new AnalysisMetric { Key = "coverage.line.percent", Value = percent, Unit = "percent", ToolName = "test" } }
    };

    private static AnalysisRunContext Context(string yaml) =>
        new(1, 2, 3, "main", new string('a', 40), "main", PipelineYaml: yaml);

    private const string ValidYaml = """
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

    // The duplication rule is a metric rule that stops after B: the validator rejects it.
    private const string OneInvalidRuleYaml = """
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
                    - key: grade.duplication
                      domain: code-quality
                      metric: duplication.percentage
                      category: duplication
                      direction: lower-is-better
                      a: 3
                      b: 5
        """;

    private const string WrongVersionYaml = """
        stages:
          - name: Quality
            steps:
              - name: Grade
                type: analysis-gate
                analysis_scope: quality
                analysis_grading:
                  version: 2
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
}
