// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data.Entities;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>Recette R-21, measured on the local run 1564: the gate said "Constats ouverts : 23" while the run listed 19
/// findings. The run's eight reports held 8, 6, 4 and 5 open findings (opengrep, trivy-dependencies, trivy-image,
/// trivy-image-front), 23 in all, but four of the image findings were seen by both image scans: 19 distinct findings.
/// The gate summed what each report counted; the list counts a finding once. A finding is now counted once by the run:
/// by the first report that holds it, each report keeping its own complete counts.</summary>
public sealed class AnalysisRunFindingCountAgreementTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    private const string CountedAndSeverityYaml = """
        stages:
          - name: Grade security
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
                      c: 1000000
                      d: 1000000
                      e: 1000000
                    - key: grade.security.high
                      domain: security
                      severity: high
                      direction: lower-is-better
                      a: 0
                      b: 0
                      c: 0
        """;

    [Fact]
    public void WithoutTheFingerprintsOfTheEarlierReports_TheRunSumsTheReports_Which_IsTheMismatchOf_Run1564()
    {
        var run = Run1564Reports();

        var measure = RunMeasure(run.Select(report => AnalysisGradeEngine.Evaluate(report, Context(), Now)));

        // 8 + 6 + 4 + 5: the four findings both image scans saw are counted twice.
        Assert.Equal(23, AnalysisFindingScore.Total(measure.FindingCounts!));
        Assert.Equal(19, DistinctOpenFindings(run));
    }

    [Fact]
    public void TheRunCountsAFindingOnce_SoTheGateAgreesWithTheListOfTheRun()
    {
        var run = Run1564Reports();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var evaluations = new List<AnalysisGradeEvaluation>();
        foreach (var report in run)
        {
            evaluations.Add(AnalysisGradeEngine.Evaluate(report, Context(), Now, new HashSet<string>(seen, StringComparer.Ordinal)));
            seen.UnionWith(report.Occurrences.Select(occurrence => occurrence.AnalysisFinding.Fingerprint));
        }

        var counted = RunMeasure(evaluations);
        var high = RunMeasure(evaluations, "grade.security.high");

        Assert.Equal(DistinctOpenFindings(run), AnalysisFindingScore.Total(counted.FindingCounts!));
        Assert.Equal(19, AnalysisFindingScore.Total(counted.FindingCounts!));
        // The same holds for a rule that counts the findings of one severity: 11 distinct high findings, not the 15 the reports add up to.
        Assert.Equal(11, high.ObservedValue);
    }

    [Fact]
    public void AReportKeepsItsOwnCompleteCounts_WhateverTheEarlierReportsHold()
    {
        var run = Run1564Reports();
        var imageFront = run[^1];
        var earlier = run[..^1].SelectMany(report => report.Occurrences)
            .Select(occurrence => occurrence.AnalysisFinding.Fingerprint).ToHashSet(StringComparer.Ordinal);

        var evaluation = AnalysisGradeEngine.Evaluate(imageFront, Context(), Now, earlier);
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<AnalysisGradeSummaryDto>(
            evaluation.SnapshotJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        var own = snapshot.Domains.SelectMany(domain => domain.Measures).Single(measure => measure.Key == "grade.security.findings");

        // Its own five findings (the report's grade and its listing are unchanged); the run takes one of them.
        Assert.Equal(5, AnalysisFindingScore.Total(own.FindingCounts!));
        Assert.Equal(1, AnalysisFindingScore.Total(own.RunFindingCounts!));
    }

    [Fact]
    public async Task ThePolicyEngineGradesAReport_WithTheFindingsTheEarlierReportsOfItsRunAlreadyHold()
    {
        var run = Run1564Reports();
        var imageFront = run[^1];
        imageFront.Id = 2052;
        imageFront.PipelineRunId = 1564;
        var repository = Substitute.For<IAnalysisRepository>();
        repository.GetApplicablePoliciesAsync(1, 2, Arg.Any<CancellationToken>()).Returns([]);
        repository.GetActiveExceptionsAsync(2, Now, Arg.Any<CancellationToken>()).Returns([]);
        repository.GetFingerprintsOfEarlierReportsAsync(1564, 2052, Arg.Any<CancellationToken>())
            .Returns(run[..^1].SelectMany(report => report.Occurrences)
                .Select(occurrence => occurrence.AnalysisFinding.Fingerprint).ToHashSet(StringComparer.Ordinal));

        var evaluation = await new AnalysisPolicyEngine(repository).EvaluateAsync(
            imageFront, Context(), Now, TestContext.Current.CancellationToken);

        // The five findings of this report, one of which no earlier report holds (5 - 4 shared = 1 for the run).
        var measure = AnalysisGradeEngine.Aggregate([evaluation.GradeSnapshotJson])!.Domains
            .SelectMany(domain => domain.Measures).Single(item => item.Key == "grade.security.findings");
        Assert.Equal(1, AnalysisFindingScore.Total(measure.FindingCounts!));
        await repository.Received(1).GetFingerprintsOfEarlierReportsAsync(1564, 2052, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ASnapshotWrittenBeforeTheRunFigureExisted_StillCountsAllItsFindings()
    {
        var run = Run1564Reports();
        var evaluation = AnalysisGradeEngine.Evaluate(run[0], Context(), Now);
        var legacyJson = System.Text.RegularExpressions.Regex.Replace(
            evaluation.SnapshotJson, "\"runFindingCounts\":(null|\\{[^}]*\\}),?", string.Empty);
        Assert.DoesNotContain("runFindingCounts", legacyJson, StringComparison.Ordinal);

        var measure = AnalysisGradeEngine.Aggregate([legacyJson])!.Domains
            .SelectMany(domain => domain.Measures).Single(item => item.Key == "grade.security.findings");

        Assert.Equal(8, AnalysisFindingScore.Total(measure.FindingCounts!));
    }

    /// <summary>The reports of run 1564 as the database held them: opengrep 8 medium findings, trivy-dependencies 6 high,
    /// trivy-image 4 high, trivy-image-front 5 high of which 4 are trivy-image's own findings (same fingerprints).</summary>
    private static List<AnalysisReport> Run1564Reports()
    {
        var shared = Enumerable.Range(1, 4).Select(index => Finding($"image-{index}", AnalysisSeverity.High)).ToList();
        return
        [
            ReportOf(2045, AnalysisCategory.Sast, Enumerable.Range(1, 8).Select(index => Finding($"sast-{index}", AnalysisSeverity.Medium))),
            ReportOf(2046, AnalysisCategory.Dependencies, Enumerable.Range(1, 6).Select(index => Finding($"dependency-{index}", AnalysisSeverity.High))),
            ReportOf(2049, AnalysisCategory.Container, shared),
            ReportOf(2052, AnalysisCategory.Container, [.. shared, Finding("image-front-only", AnalysisSeverity.High)])
        ];
    }

    private static int DistinctOpenFindings(IEnumerable<AnalysisReport> run) =>
        run.SelectMany(report => report.Occurrences).Select(occurrence => occurrence.AnalysisFinding.Fingerprint).Distinct().Count();

    private static AnalysisGradeMeasureDto RunMeasure(
        IEnumerable<AnalysisGradeEvaluation> evaluations, string key = "grade.security.findings") =>
        AnalysisGradeEngine.Aggregate(evaluations.Select(evaluation => evaluation.SnapshotJson))!.Domains
            .SelectMany(domain => domain.Measures).Single(measure => measure.Key == key);

    private static AnalysisFinding Finding(string fingerprint, AnalysisSeverity severity) => new()
    {
        Fingerprint = fingerprint,
        Severity = severity,
        Status = AnalysisFindingStatus.Open
    };

    private static AnalysisReport ReportOf(int id, AnalysisCategory category, IEnumerable<AnalysisFinding> findings)
    {
        var report = new AnalysisReport { Id = id, Category = category, Status = AnalysisReportStatus.Passed };
        foreach (var finding in findings)
        {
            finding.Category = category;
            report.Occurrences.Add(new AnalysisFindingOccurrence { IsNew = true, AnalysisFinding = finding });
        }
        return report;
    }

    private static AnalysisRunContext Context() =>
        new(1, 2, 1564, "main", new string('a', 40), "main", PipelineYaml: CountedAndSeverityYaml);
}
