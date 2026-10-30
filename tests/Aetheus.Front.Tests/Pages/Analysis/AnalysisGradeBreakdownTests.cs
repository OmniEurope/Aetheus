// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Analysis;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Analysis;

/// <summary>Display open finding counts consistently with the findings list, without weighting
/// text.</summary>
public sealed class AnalysisGradeBreakdownTests : BunitContext
{
    public AnalysisGradeBreakdownTests() => BunitTestHelper.RegisterServices(this);

    [Fact]
    public void AFindingScore_ShowsTheNumberOfOpenFindings_WithoutWeights()
    {
        // Run 2653: 1 medium and 6 low findings showed "9" (the weighted score) over a list of 7.
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters.Add(
            component => component.Grade,
            Summary(AnalysisGrade.C, new AnalysisGradeFindingCountsDto { Medium = 1, Low = 6 }, 9, weighted: true)));

        var measure = Assert.Single(cut.FindAll(".analysis-grade-measure"));
        Assert.Contains("AnalysisOpenFindingCount", measure.TextContent, StringComparison.Ordinal);
        Assert.Equal("7", measure.QuerySelector(".analysis-grade-value")!.TextContent.Trim());
        Assert.DoesNotContain("×", measure.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Enum_AnalysisSeverity", measure.TextContent, StringComparison.Ordinal);
        // The distance of a weighted score is in weighted points, not in findings: not shown.
        Assert.DoesNotContain("AnalysisDistanceToGrade", measure.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountedFindingScore_ShowsTheNumberAndTheDistanceInFindings()
    {
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters.Add(
            component => component.Grade,
            Summary(AnalysisGrade.C, new AnalysisGradeFindingCountsDto { Medium = 1, Low = 6 }, 7)));

        var measure = Assert.Single(cut.FindAll(".analysis-grade-measure"));
        Assert.Equal("7", measure.QuerySelector(".analysis-grade-value")!.TextContent.Trim());
        Assert.Contains("AnalysisDistanceToGrade", measure.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R2_139_ASealBlockedByTests_BoldsTheCodeAndTestsDomain()
    {
        var grade = new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.A,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains =
            [
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Security, Grade = AnalysisGrade.A, Completeness = AnalysisGradeCompleteness.Complete },
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.CodeQuality, Grade = AnalysisGrade.A, Completeness = AnalysisGradeCompleteness.Complete }
            ]
        };

        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters
            .Add(component => component.Grade, grade)
            .Add(component => component.SealBlockedByTests, true));

        var bold = Assert.Single(cut.FindAll(".analysis-grade-domain-limiting"));
        Assert.Equal("AnalysisCodeQualityAndTests", bold.TextContent.Trim());
    }

    [Fact]
    public void ACriticalFinding_SaysItMakesTheGradeNotDeliverable()
    {
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters.Add(
            component => component.Grade,
            Summary(AnalysisGrade.F, new AnalysisGradeFindingCountsDto { Critical = 1 }, 0)));

        Assert.Contains(
            cut.FindAll(".analysis-grade-measure-hint"),
            hint => hint.TextContent.Contains("AnalysisCriticalFindingNotDeliverable", StringComparison.Ordinal));
    }

    // Recette R-73: run #2920, an A beside a B line. The generic templates now require every rule; a pipeline that
    // marks one as not required (or a run made before) keeps the line saying it is not counted, the required line
    // beside it does not.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMeasureNotRequiredForTheGrade_SaysItIsNotCounted(bool gateRow)
    {
        var grade = new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.A,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains =
            [
                new AnalysisGradeDomainDto
                {
                    Domain = AnalysisGradeDomain.Security, Grade = AnalysisGrade.A, Required = true,
                    Completeness = AnalysisGradeCompleteness.Complete,
                    Measures =
                    [
                        new AnalysisGradeMeasureDto { Key = "grade.security.high", Severity = AnalysisSeverity.High, Required = true, Observed = true, ObservedValue = 1, Grade = AnalysisGrade.A },
                        new AnalysisGradeMeasureDto { Key = "grade.security.medium", Severity = AnalysisSeverity.Medium, Required = false, Observed = true, ObservedValue = 2, Grade = AnalysisGrade.B }
                    ]
                }
            ]
        };

        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters
            .Add(component => component.Grade, grade)
            .Add(component => component.GateRow, gateRow)
            .Add(component => component.GateGrade, AnalysisGrade.A));

        // Only the medium line (not required) carries the hint, the high line (required) does not.
        Assert.Equal(1, CountOf(cut.Markup, "GradeMeasureNotCounted"));
        var notCounted = Assert.Single(cut.FindAll(".analysis-grade-measure-hint"),
            hint => hint.TextContent.Contains("GradeMeasureNotCounted", StringComparison.Ordinal));
        var line = notCounted.Closest(gateRow ? "[data-testid='grade-row-measure']" : ".analysis-grade-measure")!;
        Assert.Contains("AnalysisMediumVulnerabilities", line.TextContent, StringComparison.Ordinal);
    }

    // Recettes R-73 and R-74 (owner decision of 2026-10-25): a domain is never "Incomplete". A required measure nobody
    // observed (its step skipped, or the repository has no such evidence) does not hide the grade of the others: the
    // line says "not measured" and names the step that should have published its evidence.
    private static AnalysisGradeSummaryDto GradeWithAMissingCoverage() => new()
    {
        OverallGrade = AnalysisGrade.B,
        Completeness = AnalysisGradeCompleteness.Incomplete,
        LimitingDomain = AnalysisGradeDomain.Architecture,
        Domains =
        [
            new AnalysisGradeDomainDto
            {
                Domain = AnalysisGradeDomain.Architecture, Grade = AnalysisGrade.B, Required = true,
                Completeness = AnalysisGradeCompleteness.Complete,
                Measures =
                [
                    SeverityMeasure("grade.architecture.medium", AnalysisSeverity.Medium, 2, AnalysisGrade.B) with { Required = true }
                ]
            },
            new AnalysisGradeDomainDto
            {
                Domain = AnalysisGradeDomain.Reliability, Grade = null, Required = true,
                Completeness = AnalysisGradeCompleteness.Incomplete,
                Measures =
                [
                    new AnalysisGradeMeasureDto
                    {
                        Key = "grade.coverage.line", Domain = AnalysisGradeDomain.Reliability, Required = true, Observed = false,
                        ProducerStep = "Publish coverage evidence"
                    }
                ]
            }
        ]
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMissingRequiredMeasure_SaysNotMeasuredAndNamesItsStep_NeverIncomplete(bool gateRow)
    {
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters
            .Add(component => component.Grade, GradeWithAMissingCoverage())
            .Add(component => component.GateRow, gateRow)
            .Add(component => component.GateGrade, AnalysisGrade.B));

        Assert.DoesNotContain("Incomplete", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("NotEvaluated", cut.Markup, StringComparison.Ordinal);
        var line = cut.FindAll(gateRow ? "[data-testid='grade-row-measure']" : ".analysis-grade-measure")
            .Single(element => element.TextContent.Contains("AnalysisTestedLines", StringComparison.Ordinal));
        Assert.Contains("GradeMeasureNotMeasured", line.TextContent, StringComparison.Ordinal);
        var hint = line.QuerySelector("[data-testid='grade-row-measure-missing']")!;
        Assert.Contains("GradeMeasureMissingStep Publish coverage evidence", hint.TextContent, StringComparison.Ordinal);
        // The missing measure is not a "not counted" one: it counts, it was just not measured.
        Assert.DoesNotContain("GradeMeasureNotCounted", line.TextContent, StringComparison.Ordinal);
    }

    // Run #2958: a grading rule the validator rejected is an F; its line says it was rejected and why.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARejectedRule_SaysItWasRejectedAndWhy(bool gateRow)
    {
        var grade = new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.F,
            Completeness = AnalysisGradeCompleteness.Complete,
            LimitingDomain = AnalysisGradeDomain.CodeQuality,
            Domains =
            [
                new AnalysisGradeDomainDto
                {
                    Domain = AnalysisGradeDomain.CodeQuality, Grade = AnalysisGrade.F, Required = true,
                    Completeness = AnalysisGradeCompleteness.Complete,
                    Measures =
                    [
                        new AnalysisGradeMeasureDto
                        {
                            Key = "grade.duplication", Domain = AnalysisGradeDomain.CodeQuality, Required = true, Observed = true,
                            Grade = AnalysisGrade.F, Rejection = "analysis grading rule 'grade.duplication' must define finite A, B, C, D and E thresholds."
                        }
                    ]
                }
            ]
        };

        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters
            .Add(component => component.Grade, grade)
            .Add(component => component.GateRow, gateRow)
            .Add(component => component.GateGrade, AnalysisGrade.F));

        var line = cut.FindAll(gateRow ? "[data-testid='grade-row-measure']" : ".analysis-grade-measure")
            .Single(element => element.TextContent.Contains("AnalysisDuplicatedCode", StringComparison.Ordinal));
        Assert.Contains("GradeMeasureRejected", line.TextContent, StringComparison.Ordinal);
        var hint = line.QuerySelector("[data-testid='grade-row-measure-rejected']")!;
        Assert.Contains("must define finite A, B, C, D and E thresholds", hint.TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AGradeOnPartOfItsMeasures_KeepsItsLimitingDomain(bool gateRow)
    {
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters
            .Add(component => component.Grade, GradeWithAMissingCoverage())
            .Add(component => component.GateRow, gateRow)
            .Add(component => component.GateGrade, AnalysisGrade.B));

        var limiting = Assert.Single(cut.FindAll(".analysis-grade-domain-limiting"));
        Assert.Contains("AnalysisCodeArchitecture", limiting.TextContent, StringComparison.Ordinal);
        Assert.Equal("grade.architecture.medium", AnalysisGradeBreakdown.FirstLimitingMeasure(GradeWithAMissingCoverage())?.Key);
    }

    [Fact]
    public void TheMissingMeasures_AreCounted_OnlyWhenTheGradeRequiresThem()
    {
        var grade = GradeWithAMissingCoverage();
        Assert.Equal(1, AnalysisGradeBreakdown.MissingMeasureCount(grade));
        Assert.Equal(0, AnalysisGradeBreakdown.MissingMeasureCount(null));

        var optional = grade with
        {
            Domains =
            [
                new AnalysisGradeDomainDto
                {
                    Domain = AnalysisGradeDomain.Reliability,
                    Measures = [new AnalysisGradeMeasureDto { Key = "grade.coverage.line", Required = false, Observed = false }]
                }
            ]
        };
        Assert.Equal(0, AnalysisGradeBreakdown.MissingMeasureCount(optional));
    }

    // Owner decision of 2026-10-25 on the sealed candidate contract: a grade computed on part of its measures does not
    // qualify the release, and its deployment then asks for the extra approval (the R-24 stage). The Gate tab says so.
    [Fact]
    public void APartialGrade_OnTheGateRow_SaysItDoesNotQualifyTheRelease()
    {
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters
            .Add(component => component.Grade, GradeWithAMissingCoverage())
            .Add(component => component.GateRow, true)
            .Add(component => component.GateGrade, AnalysisGrade.B));

        var sentence = Assert.Single(cut.FindAll("[data-testid='grade-row-gate-partial']"));
        Assert.Contains("GateGradePartialNotQualifying", sentence.TextContent, StringComparison.Ordinal);
        Assert.NotNull(sentence.Closest("[data-testid='grade-row-gate']"));
        Assert.True(AnalysisGradeBreakdown.IsPartialGrade(GradeWithAMissingCoverage()));
    }

    [Fact]
    public void AGradeOnEveryMeasure_OnTheGateRow_SaysNothingAboutQualifying()
    {
        var complete = GradeWithAMissingCoverage() with { Completeness = AnalysisGradeCompleteness.Complete };

        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters
            .Add(component => component.Grade, complete)
            .Add(component => component.GateRow, true)
            .Add(component => component.GateGrade, AnalysisGrade.B));

        Assert.Empty(cut.FindAll("[data-testid='grade-row-gate-partial']"));
        Assert.DoesNotContain("GateGradePartialNotQualifying", cut.Markup, StringComparison.Ordinal);
        Assert.False(AnalysisGradeBreakdown.IsPartialGrade(complete));
    }

    [Fact]
    public void ANoMeasureGrade_IsNotAPartialGrade_SoItSaysNothingAboutQualifying()
    {
        // Nothing was graded at all: there is no letter that could be partial.
        var nothing = new AnalysisGradeSummaryDto
        {
            OverallGrade = null,
            Completeness = AnalysisGradeCompleteness.Incomplete,
            Domains = [new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Security, Grade = null, Required = true, Completeness = AnalysisGradeCompleteness.Incomplete }]
        };

        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters
            .Add(component => component.Grade, nothing)
            .Add(component => component.GateRow, true)
            .Add(component => component.GateGrade, AnalysisGrade.A));

        Assert.Empty(cut.FindAll("[data-testid='grade-row-gate-partial']"));
        Assert.False(AnalysisGradeBreakdown.IsPartialGrade(nothing));
        Assert.False(AnalysisGradeBreakdown.IsPartialGrade(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARequiredDomainNobodyMeasured_SaysNoMeasure_NotIncomplete(bool gateRow)
    {
        var grade = new AnalysisGradeSummaryDto
        {
            OverallGrade = null,
            Completeness = AnalysisGradeCompleteness.Incomplete,
            Domains = [new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Security, Grade = null, Required = true, Completeness = AnalysisGradeCompleteness.Incomplete }]
        };

        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters
            .Add(component => component.Grade, grade)
            .Add(component => component.GateRow, gateRow)
            .Add(component => component.GateGrade, AnalysisGrade.A));

        Assert.Equal(1, CountOf(cut.Markup, ">AnalysisGradeNoMeasure<"));
        Assert.DoesNotContain("Incomplete", cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADomainNotRequiredAndWithNothingToShow_IsNotListed(bool gateRow)
    {
        var grade = new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.A,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains =
            [
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Security, Grade = AnalysisGrade.A, Required = true, Completeness = AnalysisGradeCompleteness.Complete },
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Architecture, Grade = null, Required = false, Completeness = AnalysisGradeCompleteness.Incomplete }
            ]
        };

        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters
            .Add(component => component.Grade, grade)
            .Add(component => component.GateRow, gateRow)
            .Add(component => component.GateGrade, AnalysisGrade.A));

        Assert.Contains("AnalysisApplicationSecurity", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("AnalysisCodeArchitecture", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("NotEvaluated", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Incomplete", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSeverityLines_ReadFromTheWorstToTheMildest_WhateverTheOrderOfTheirKeys()
    {
        // Alphabetical order of the keys: critical, high, info, low, medium.
        var shown = AnalysisGradeBreakdown.ShownMeasures(
        [
            SeverityMeasure("grade.architecture.critical", AnalysisSeverity.Critical, 1, AnalysisGrade.F),
            SeverityMeasure("grade.architecture.high", AnalysisSeverity.High, 1, AnalysisGrade.D),
            SeverityMeasure("grade.architecture.info", AnalysisSeverity.Info, 2, AnalysisGrade.B),
            SeverityMeasure("grade.architecture.low", AnalysisSeverity.Low, 2, AnalysisGrade.B),
            SeverityMeasure("grade.architecture.medium", AnalysisSeverity.Medium, 2, AnalysisGrade.B)
        ]);

        Assert.Equal(
            ["grade.architecture.critical", "grade.architecture.high", "grade.architecture.medium", "grade.architecture.low", "grade.architecture.info"],
            shown.Select(measure => measure.Key));
    }

    [Fact]
    public void ALineThatIsNotASeverityLine_KeepsItsPlaceBetweenTheSeverityLines()
    {
        var shown = AnalysisGradeBreakdown.ShownMeasures(
        [
            SeverityMeasure("grade.security.critical", AnalysisSeverity.Critical, 1, AnalysisGrade.F),
            Measure("grade.coverage.line", 50, AnalysisGrade.D),
            SeverityMeasure("grade.security.high", AnalysisSeverity.High, 1, AnalysisGrade.D),
            SeverityMeasure("grade.security.info", AnalysisSeverity.Info, 1, AnalysisGrade.B),
            SeverityMeasure("grade.security.medium", AnalysisSeverity.Medium, 1, AnalysisGrade.B)
        ]);

        Assert.Equal(
            ["grade.security.critical", "grade.coverage.line", "grade.security.high", "grade.security.medium", "grade.security.info"],
            shown.Select(measure => measure.Key));
    }

    private static int CountOf(string text, string word) => text.Split(word, StringSplitOptions.None).Length - 1;

    private static AnalysisGradeSummaryDto Summary(
        AnalysisGrade grade, AnalysisGradeFindingCountsDto counts, double score, bool weighted = false) => new()
        {
            OverallGrade = grade,
            Completeness = AnalysisGradeCompleteness.Complete,
            LimitingDomain = AnalysisGradeDomain.Security,
            Domains =
        [
            new AnalysisGradeDomainDto
            {
                Domain = AnalysisGradeDomain.Security,
                Grade = grade,
                Required = true,
                Completeness = AnalysisGradeCompleteness.Complete,
                Measures =
                [
                    new AnalysisGradeMeasureDto
                    {
                        Key = "grade.security.findings",
                        Domain = AnalysisGradeDomain.Security,
                        Grade = grade,
                        Required = true,
                        Observed = true,
                        ObservedValue = score,
                        Direction = AnalysisMetricDirection.LowerIsBetter,
                        AThreshold = 0,
                        BThreshold = 5,
                        CThreshold = 15,
                        DThreshold = 30,
                        WeightedFindingScore = weighted,
                        CountedFindingScore = !weighted,
                        NextGrade = grade > AnalysisGrade.A ? grade - 1 : null,
                        DistanceToNextGrade = grade > AnalysisGrade.A ? 2 : null,
                        FindingCounts = counts
                    }
                ]
            }
        ]
        };

    private static AnalysisGradeMeasureDto Measure(string key, double value, AnalysisGrade grade) => new()
    {
        Key = key,
        Observed = true,
        ObservedValue = value,
        Grade = grade
    };

    [Fact]
    public void TheFirstCause_IsTheFirstMeasureCarryingTheOverallGrade_InDisplayOrder()
    {
        // Recette R1-60: one cause beside the grade, chosen in a stable order, never a tie list.
        var grade = new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.C,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains =
            [
                new AnalysisGradeDomainDto
                {
                    Domain = AnalysisGradeDomain.Architecture, Grade = AnalysisGrade.C,
                    Measures = [Measure("grade.architecture.cycles", 46, AnalysisGrade.C)]
                },
                new AnalysisGradeDomainDto
                {
                    Domain = AnalysisGradeDomain.CodeQuality, Grade = AnalysisGrade.C,
                    Measures = [Measure("grade.duplication", 3, AnalysisGrade.A), Measure("grade.complexity.maximum", 22, AnalysisGrade.C)]
                }
            ]
        };

        var cause = AnalysisGradeBreakdown.FirstLimitingMeasure(grade);

        Assert.Equal("grade.complexity.maximum", cause?.Key);
    }

    [Fact]
    public void AnAGrade_HasNoCause()
    {
        var grade = new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.A,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains = [new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Security, Grade = AnalysisGrade.A }]
        };

        Assert.Null(AnalysisGradeBreakdown.FirstLimitingMeasure(grade));
    }

    [Theory]
    [InlineData("grade.security.critical", 0, false)]
    [InlineData("grade.architecture.cycles", 0, false)]
    [InlineData("grade.architecture.cycles", 2, true)]
    [InlineData("grade.coverage.line", 0, true)]
    [InlineData("grade.complexity.maximum", 0, true)]
    public void ACountAtZero_IsHidden_TheTwoKeptMeasuresAreNot(string key, double value, bool shown)
    {
        // Recette R1-61: zero counts say nothing; tested lines and the most complex path stay even at zero.
        Assert.Equal(shown, AnalysisGradeBreakdown.IsShown(Measure(key, value, AnalysisGrade.A)));
    }

    private static AnalysisGradeMeasureDto OpenFindingsMeasure(AnalysisGradeFindingCountsDto counts, AnalysisGrade grade) => new()
    {
        Key = "grade.security.findings",
        Observed = true,
        ObservedValue = AnalysisFindingScore.Total(counts),
        Grade = grade,
        CountedFindingScore = true,
        FindingCounts = counts
    };

    private static AnalysisGradeMeasureDto SeverityMeasure(string key, AnalysisSeverity severity, double value, AnalysisGrade grade) => new()
    {
        Key = key,
        Severity = severity,
        Observed = true,
        ObservedValue = value,
        Grade = grade
    };

    [Fact]
    public void R2_TheOpenFindingsLine_IsLeftOut_WhenTheMediumLineSaysTheSame()
    {
        // Recette R-2 (Gate #2857): "Constats ouverts 2" and "Failles de sévérité moyenne 2" were one fact.
        var shown = AnalysisGradeBreakdown.ShownMeasures(
        [
            OpenFindingsMeasure(new AnalysisGradeFindingCountsDto { Medium = 2 }, AnalysisGrade.B),
            SeverityMeasure("grade.security.medium", AnalysisSeverity.Medium, 2, AnalysisGrade.B)
        ]);

        Assert.Equal(["grade.security.medium"], shown.Select(measure => measure.Key));
    }

    [Fact]
    public void R2_TheOpenFindingsLine_IsKept_WhenItCountsWhatNoSeverityLineDoes()
    {
        // A low finding sits in no severity line: dropping the open-findings line would hide the measure that grades.
        var shown = AnalysisGradeBreakdown.ShownMeasures(
        [
            OpenFindingsMeasure(new AnalysisGradeFindingCountsDto { Medium = 2, Low = 1 }, AnalysisGrade.B),
            SeverityMeasure("grade.security.medium", AnalysisSeverity.Medium, 2, AnalysisGrade.B)
        ]);

        Assert.Equal(["grade.security.findings", "grade.security.medium"], shown.Select(measure => measure.Key));
    }

    [Fact]
    public void R2_TheOpenFindingsLine_IsKept_WhenItsLetterDiffersFromTheMediumLine()
    {
        var shown = AnalysisGradeBreakdown.ShownMeasures(
        [
            OpenFindingsMeasure(new AnalysisGradeFindingCountsDto { Medium = 2 }, AnalysisGrade.C),
            SeverityMeasure("grade.security.medium", AnalysisSeverity.Medium, 2, AnalysisGrade.A)
        ]);

        Assert.Equal(2, shown.Count);
    }

    [Theory]
    [InlineData("grade.security.critical", "AnalysisCriticalVulnerabilities")]
    [InlineData("grade.security.high", "AnalysisHighVulnerabilities")]
    [InlineData("grade.security.medium", "AnalysisMediumVulnerabilities")]
    [InlineData("grade.security.low", "AnalysisLowVulnerabilities")]
    [InlineData("grade.security.info", "AnalysisInfoFindings")]
    [InlineData("grade.architecture.critical", "AnalysisArchitectureCritical")]
    [InlineData("grade.architecture.high", "AnalysisArchitectureHigh")]
    [InlineData("grade.architecture.medium", "AnalysisArchitectureMedium")]
    [InlineData("grade.architecture.low", "AnalysisArchitectureLow")]
    [InlineData("grade.architecture.info", "AnalysisArchitectureInfo")]
    [InlineData("grade.security.findings", "AnalysisOpenFindingCount")]
    [InlineData("grade.quality.findings", "AnalysisOpenFindingCount")]
    [InlineData("grade.coverage.line", "AnalysisTestedLines")]
    [InlineData("grade.coverage.branch", "AnalysisTestedBranches")]
    [InlineData("grade.complexity.maximum", "AnalysisMaximumComplexity")]
    [InlineData("grade.duplication", "AnalysisDuplicatedCode")]
    [InlineData("grade.architecture.cycles", "AnalysisDependencyCycles")]
    public void MeasureLabel_AKnownMeasure_UsesItsLocalizedLabel(string key, string resource) =>
        Assert.Equal(resource, AnalysisGradeBreakdown.MeasureLabel(new BunitTestHelper.StubLocalizer(), key));

    [Theory]
    [InlineData("grade.new.measure", "new measure")]
    [InlineData("GRADE.Other.Thing", "Other Thing")]
    [InlineData("coverage.line.percent", "coverage.line.percent")]
    public void MeasureLabel_AnUnknownMeasure_IsReadableWithoutItsGradePrefix(string key, string expected) =>
        Assert.Equal(expected, AnalysisGradeBreakdown.MeasureLabel(new BunitTestHelper.StubLocalizer(), key));

    [Theory]
    [InlineData(AnalysisGradeDomain.Security, "AnalysisApplicationSecurity")]
    [InlineData(AnalysisGradeDomain.Reliability, "AnalysisCodeQualityAndTests")]
    [InlineData(AnalysisGradeDomain.CodeQuality, "AnalysisCodeQualityAndTests")]
    [InlineData(AnalysisGradeDomain.Architecture, "AnalysisCodeArchitecture")]
    [InlineData(AnalysisGradeDomain.Performance, "AnalysisRuntimePerformance")]
    [InlineData(null, "NotAvailable")]
    public void DomainLabel_EachDomain_UsesItsLocalizedLabel(AnalysisGradeDomain? domain, string resource) =>
        Assert.Equal(resource, AnalysisGradeBreakdown.DomainLabel(new BunitTestHelper.StubLocalizer(), domain));

    [Fact]
    public void R2_139_DomainsTiedOnTheOverallLetter_AreAllBold()
    {
        // Every domain that sets the overall letter is bold, not only the first one.
        var grade = new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.C,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains =
            [
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Security, Grade = AnalysisGrade.C, Completeness = AnalysisGradeCompleteness.Complete },
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.CodeQuality, Grade = AnalysisGrade.B, Completeness = AnalysisGradeCompleteness.Complete },
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Architecture, Grade = AnalysisGrade.C, Completeness = AnalysisGradeCompleteness.Complete }
            ]
        };

        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters.Add(component => component.Grade, grade));

        Assert.Equal(
            ["AnalysisApplicationSecurity", "AnalysisCodeArchitecture"],
            cut.FindAll(".analysis-grade-domain-limiting").Select(element => element.TextContent.Trim()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void R2_141_ACompleteDomain_HasNoRequiredCompleteFreshnessLine_NorSeverityCounts()
    {
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters.Add(
            component => component.Grade,
            Summary(AnalysisGrade.C, new AnalysisGradeFindingCountsDto { High = 1, Medium = 2, Low = 3, Info = 1 }, 7)));

        Assert.Empty(cut.FindAll(".analysis-grade-domain-state"));
        foreach (var removed in new[] { "AnalysisRequired", "AnalysisAdvisory", "AnalysisFreshness", "Complete", "Enum_AnalysisSeverity" })
            Assert.DoesNotContain(removed, cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void R2_142_ThePerformanceDomain_ShowsOnlyOnceAMeasureIsObserved(bool observed, bool shown)
    {
        var grade = new AnalysisGradeSummaryDto
        {
            OverallGrade = AnalysisGrade.A,
            Completeness = AnalysisGradeCompleteness.Complete,
            Domains =
            [
                new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Security, Grade = AnalysisGrade.A, Completeness = AnalysisGradeCompleteness.Complete },
                new AnalysisGradeDomainDto
                {
                    Domain = AnalysisGradeDomain.Performance,
                    Grade = AnalysisGrade.A,
                    Completeness = AnalysisGradeCompleteness.Complete,
                    Measures = [new AnalysisGradeMeasureDto { Key = "grade.performance.p95", Domain = AnalysisGradeDomain.Performance, Observed = observed, ObservedValue = observed ? 120 : null, Grade = AnalysisGrade.A }]
                }
            ]
        };

        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters.Add(component => component.Grade, grade));

        Assert.Equal(shown, cut.Markup.Contains("AnalysisRuntimePerformance", StringComparison.Ordinal));
    }

    private static AnalysisGradeSummaryDto CoverageGrade() => new()
    {
        OverallGrade = AnalysisGrade.B,
        Completeness = AnalysisGradeCompleteness.Complete,
        Domains =
        [
            new AnalysisGradeDomainDto
            {
                Domain = AnalysisGradeDomain.CodeQuality, Grade = AnalysisGrade.B, Required = true, Completeness = AnalysisGradeCompleteness.Complete,
                Measures =
                [
                    new AnalysisGradeMeasureDto
                    {
                        Key = "grade.coverage.line", Grade = AnalysisGrade.B, Observed = true, ObservedValue = 88.64, Unit = "percent",
                        Direction = AnalysisMetricDirection.HigherIsBetter, AThreshold = 90, BThreshold = 80, CThreshold = 70, DThreshold = 60,
                        NextGrade = AnalysisGrade.A, DistanceToNextGrade = 1.36
                    }
                ]
            }
        ]
    };

    [Fact]
    public void R1_38_EveryRowOfTheBlock_ExplainsItsCriterionInATooltip_AndOpensItsDetailOnAClick()
    {
        var cut = Render<AnalysisGradeBreakdown>(parameters => parameters
            .Add(component => component.Grade, CoverageGrade())
            .Add(component => component.GateRow, true)
            .Add(component => component.GateGrade, AnalysisGrade.B));

        // The block is one OE accordion; the Gate row, the domain and the measure each carry an OE help mark
        // (a tooltip) that explains the criterion.
        Assert.Single(cut.FindAll("[data-testid='analysis-grade-block'].omni-accordion"));
        Assert.Equal(3, cut.FindAll(".analysis-grade-row .omni-accordion__help.omni-tooltip").Count);
        Assert.Contains("AnalysisCriterionGate", cut.Find("[data-testid='grade-row-gate']").InnerHtml, StringComparison.Ordinal);
        Assert.Contains("AnalysisCriterionCoverageLine", cut.Find("[data-testid='grade-row-measure']").InnerHtml, StringComparison.Ordinal);
        // An OE accordion row per line, folded by default (no detail rendered); a click on a row opens it and the
        // detail names the thresholds and the measure. Rows open independently of each other (Multiple).
        var toggles = cut.FindAll(".analysis-grade-row .omni-accordion__toggle");
        Assert.Equal(3, toggles.Count);
        Assert.All(toggles, toggle => Assert.Equal("false", toggle.GetAttribute("aria-expanded")));
        Assert.Empty(cut.FindAll(".analysis-grade-row-detail"));
        cut.Find("[data-testid='grade-row-gate'] .omni-accordion__toggle").Click();
        cut.Find("[data-testid='grade-row-measure'] .omni-accordion__toggle").Click();
        Assert.Equal("true", cut.Find("[data-testid='grade-row-gate'] .omni-accordion__toggle").GetAttribute("aria-expanded"));
        Assert.Equal("true", cut.Find("[data-testid='grade-row-measure'] .omni-accordion__toggle").GetAttribute("aria-expanded"));
        var detail = cut.Find("[data-testid='grade-row-measure'] .analysis-grade-row-detail");
        Assert.Contains("AnalysisGradeThresholdsHigher", detail.TextContent, StringComparison.Ordinal);
        Assert.Contains("AnalysisGradeMeasuredValue", detail.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_38_ThresholdText_ListsTheLettersInTheDirectionThatIsBetter_WithThePercentUnit()
    {
        var higher = new AnalysisGradeMeasureDto
        {
            Unit = "percent",
            Direction = AnalysisMetricDirection.HigherIsBetter,
            AThreshold = 75,
            BThreshold = 65,
            CThreshold = 55,
            DThreshold = 40,
            EThreshold = 20
        };
        var lower = new AnalysisGradeMeasureDto { Direction = AnalysisMetricDirection.LowerIsBetter, AThreshold = 0, BThreshold = 5, CThreshold = 15, DThreshold = 30 };

        Assert.Equal("A ≥ 75 %, B ≥ 65 %, C ≥ 55 %, D ≥ 40 %, E ≥ 20 %", AnalysisGradeBreakdown.ThresholdText(higher));
        Assert.Equal("A ≤ 0, B ≤ 5, C ≤ 15, D ≤ 30, E > 30", AnalysisGradeBreakdown.ThresholdText(lower));
        Assert.Null(AnalysisGradeBreakdown.ThresholdText(new AnalysisGradeMeasureDto()));
    }

    [Fact]
    public void R73_ThresholdText_SaysWhatLiesBeyondTheLastThreshold_WhenARuleStopsEarly()
    {
        // Owner decision of 2026-10-25: any high is a D whatever the count, six medium or more a C, never worse.
        var high = new AnalysisGradeMeasureDto
        {
            Direction = AnalysisMetricDirection.LowerIsBetter,
            AThreshold = 0,
            BThreshold = 0,
            CThreshold = 0
        };
        var medium = new AnalysisGradeMeasureDto
        {
            Direction = AnalysisMetricDirection.LowerIsBetter,
            AThreshold = 0,
            BThreshold = 5
        };

        Assert.Equal("A ≤ 0, B ≤ 0, C ≤ 0, D > 0", AnalysisGradeBreakdown.ThresholdText(high));
        Assert.Equal("A ≤ 0, B ≤ 5, C > 5", AnalysisGradeBreakdown.ThresholdText(medium));
    }

    [Fact]
    public void R1_38_ACoverageMeasureWithItsPercentUnit_ReadsWithThePercentSign()
    {
        Assert.Equal("88.64 %", AnalysisGradeBreakdown.MeasureText(CoverageGrade().Domains[0].Measures[0]).Replace(',', '.'));
    }
}
