// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

public sealed class PipelineAnalysisGradingValidatorTests
{
    [Fact]
    public void Validate_AcceptsVersionedCoverageBands()
    {
        var grading = CoverageGrading();

        var errors = PipelineAnalysisGradingValidator.Validate("quality", grading);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_RejectsNonMonotonicBandsAndMissingRequiredDomainRule()
    {
        var grading = CoverageGrading() with
        {
            RequiredDomains = ["reliability", "architecture"],
            Rules =
            [
                CoverageGrading().Rules[0] with
                {
                    A = 75,
                    B = 80
                }
            ]
        };

        var errors = PipelineAnalysisGradingValidator.Validate("quality", grading);

        Assert.Contains(errors, error => error.Contains("not monotonic", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "required domain 'architecture' has no grading rule",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsAWeightedFindingScoreWithAnOpenEBand()
    {
        var errors = PipelineAnalysisGradingValidator.Validate("security", WeightedGrading());

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_AcceptsACountedFindingScoreWithAnOpenEBand()
    {
        // Recette R1-4: finding_score: count grades the number of open findings, no weighting.
        var grading = WeightedGrading();
        grading = grading with { Rules = [grading.Rules[0] with { FindingScore = "count" }] };

        Assert.Empty(PipelineAnalysisGradingValidator.Validate("security", grading));
    }

    [Fact]
    public void Validate_AcceptsAWeightedFindingScoreWithAnEThreshold()
    {
        var grading = WeightedGrading();
        grading = grading with { Rules = [grading.Rules[0] with { E = 60 }] };

        Assert.Empty(PipelineAnalysisGradingValidator.Validate("security", grading));
    }

    [Fact]
    public void Validate_RequiresTheEThresholdOutsideAWeightedFindingScore()
    {
        var grading = CoverageGrading() with { Rules = [CoverageGrading().Rules[0] with { E = null }] };

        var errors = PipelineAnalysisGradingValidator.Validate("quality", grading);

        Assert.Contains(errors, error => error.Contains("must define finite A, B, C, D and E", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsAnUnknownFindingScore()
    {
        var grading = WeightedGrading();
        grading = grading with { Rules = [grading.Rules[0] with { FindingScore = "average" }] };

        var errors = PipelineAnalysisGradingValidator.Validate("security", grading);

        Assert.Contains(errors, error => error.Contains("finding_score must be 'count' or 'weighted'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsAFindingScoreCombinedWithASeverity()
    {
        var grading = WeightedGrading();
        grading = grading with { Rules = [grading.Rules[0] with { Severity = "high" }] };

        var errors = PipelineAnalysisGradingValidator.Validate("security", grading);

        Assert.Contains(errors, error => error.Contains("exactly one metric, finding severity or finding score", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsAFindingScoreGradedHigherIsBetter()
    {
        var grading = WeightedGrading();
        grading = grading with
        {
            Rules = [grading.Rules[0] with { Direction = "higher-is-better", A = 30, B = 15, C = 5, D = 0 }]
        };

        var errors = PipelineAnalysisGradingValidator.Validate("security", grading);

        Assert.Contains(errors, error => error.Contains("finding score must be graded lower-is-better", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsAWeightedFindingScoreWithoutItsDThreshold()
    {
        var grading = WeightedGrading();
        grading = grading with { Rules = [grading.Rules[0] with { D = null }] };

        var errors = PipelineAnalysisGradingValidator.Validate("security", grading);

        Assert.Contains(errors, error => error.Contains("must define finite A, B, C and D thresholds", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseAndValidate_DeserializesAWeightedFindingScore()
    {
        const string yaml = """
            stages:
              - name: Analyze
                steps:
                  - name: Security gate
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

        var definition = YamlParsingHelper.ParseAndValidate(yaml);
        var grading = Assert.Single(Assert.Single(definition!.Stages).Steps).AnalysisGrading!;

        Assert.Equal("weighted", Assert.Single(grading.Rules).FindingScore);
        Assert.Empty(PipelineAnalysisGradingValidator.Validate("security", grading));
    }

    [Fact]
    public void ParseAndValidate_DeserializesAnalysisGrading()
    {
        const string yaml = """
            stages:
              - name: Analyze
                steps:
                  - name: Quality gate
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
            """;

        var definition = YamlParsingHelper.ParseAndValidate(yaml);
        var grading = Assert.Single(Assert.Single(definition!.Stages).Steps).AnalysisGrading;

        Assert.NotNull(grading);
        Assert.Equal("C", grading.MinimumGrade);
        Assert.Equal(["reliability"], grading.RequiredDomains);
        Assert.Equal(75, Assert.Single(grading.Rules).A);
    }

    private static PipelineAnalysisGradingDefinition WeightedGrading() => new()
    {
        MinimumGrade = "F",
        RequiredDomains = ["security"],
        Rules =
        [
            new PipelineAnalysisGradeRuleDefinition
            {
                Key = "grade.security.findings",
                Domain = "security",
                FindingScore = "weighted",
                Direction = "lower-is-better",
                A = 0,
                B = 5,
                C = 15,
                D = 30
            }
        ]
    };

    private static PipelineAnalysisGradingDefinition CoverageGrading() => new()
    {
        MinimumGrade = "C",
        RequiredDomains = ["reliability"],
        Rules =
        [
            new PipelineAnalysisGradeRuleDefinition
            {
                Key = "grade.coverage.line",
                Domain = "reliability",
                Metric = "coverage.line.percent",
                Category = "coverage",
                Direction = "higher-is-better",
                A = 75,
                B = 65,
                C = 55,
                D = 40,
                E = 20
            }
        ]
    };
}
