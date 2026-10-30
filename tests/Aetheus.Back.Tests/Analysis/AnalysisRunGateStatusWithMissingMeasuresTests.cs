// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>Recette R-73, R-74: a grade resting on part of the required measures is still a grade and the gate is not an
/// error for it (the measures nobody observed are listed with their step); a gate for which no grade can be given at all
/// still is an error. A producer that ran and published nothing stays a missing producer, which is an error as before.</summary>
public sealed class AnalysisRunGateStatusWithMissingMeasuresTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private const string CoverageAndArchitectureYaml = """
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
                  required_domains: [reliability, architecture]
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
                    - key: grade.architecture.medium
                      domain: architecture
                      severity: medium
                      category: architecture
                      direction: lower-is-better
                      a: 0
                      b: 5
                      c: 15
                      d: 30
                      e: 60
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

    private async Task SeedArchitectureReportAsync(string yaml)
    {
        _db.Organizations.Add(new Organization { Id = 1, Name = "Aetheus", Slug = "aetheus" });
        _db.Projects.Add(new Project { Id = 1, Name = "Project", OrganizationId = 1, DefaultBranch = "main" });
        _db.Pipelines.Add(new Pipeline { Id = 1, Name = "quality", ProjectId = 1 });
        _db.PipelineRuns.Add(new PipelineRun { Id = 1, PipelineId = 1, Status = PipelineStatus.Success, BranchName = "main", CommitHash = new string('a', 40) });
        var report = new AnalysisReport
        {
            Id = 1,
            OrganizationId = 1,
            ProjectId = 1,
            PipelineRunId = 1,
            ScannerKey = "dotnet-architecture",
            ScannerName = "Architecture",
            ScannerVersion = "1.0.0",
            Category = AnalysisCategory.Architecture,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.Sarif,
            ContentHash = 1.ToString("D64", System.Globalization.CultureInfo.InvariantCulture),
            StartedAt = Now.AddMinutes(-1),
            CompletedAt = Now,
            CreatedAt = Now
        };
        for (var index = 0; index < 2; index++)
        {
            report.Occurrences.Add(new AnalysisFindingOccurrence
            {
                IsNew = true,
                AnalysisFinding = new AnalysisFinding
                {
                    Fingerprint = $"architecture-{index}",
                    Severity = AnalysisSeverity.Medium,
                    Status = AnalysisFindingStatus.Open,
                    Category = AnalysisCategory.Architecture
                }
            });
        }
        var grade = AnalysisGradeEngine.Evaluate(
            report, new AnalysisRunContext(1, 1, 1, "main", new string('a', 40), "main", PipelineYaml: yaml), Now);
        _db.AnalysisReports.Add(new AnalysisReport
        {
            Id = report.Id,
            OrganizationId = 1,
            ProjectId = 1,
            PipelineRunId = 1,
            ScannerKey = report.ScannerKey,
            ScannerName = report.ScannerName,
            ScannerVersion = report.ScannerVersion,
            Category = report.Category,
            Status = report.Status,
            Format = report.Format,
            ContentHash = report.ContentHash,
            StartedAt = report.StartedAt,
            CompletedAt = report.CompletedAt,
            CreatedAt = Now
        });
        _db.AnalysisEvaluations.Add(new AnalysisEvaluation
        {
            Id = 1,
            OrganizationId = 1,
            ProjectId = 1,
            AnalysisReportId = 1,
            PipelineRunId = 1,
            Status = AnalysisGateStatus.Passed,
            Grade = grade.Grade,
            GradeCompleteness = grade.Completeness,
            GradeSnapshotJson = grade.SnapshotJson,
            GradeSnapshotHash = grade.SnapshotHash,
            EvaluatedAt = Now,
            CreatedAt = Now
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AGradeRestingOnPartOfTheRequiredMeasures_IsAGradeAndTheGateIsNotAnError()
    {
        // The coverage step published nothing, the architecture step graded two medium findings: a B, with the
        // coverage listed as missing. Before R-73 and R-74 the gate was an error and the grade was hidden.
        await SeedArchitectureReportAsync(CoverageAndArchitectureYaml);

        var gate = await new AnalysisRepository(_db).GetRunGateAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Passed, gate.Status);
        Assert.Equal(AnalysisGrade.B, gate.Grade!.OverallGrade);
        Assert.Equal(AnalysisGradeCompleteness.Incomplete, gate.Grade.Completeness);
        var coverage = gate.Grade.Domains.SelectMany(domain => domain.Measures).Single(measure => measure.Key == "grade.coverage.line");
        Assert.False(coverage.Observed);
        Assert.Equal("Publish coverage evidence", coverage.ProducerStep);
    }

    [Fact]
    public async Task WhenNoGradeCanBeGivenAtAll_TheGateIsStillAnError()
    {
        // Only the coverage is graded and nothing published it: no letter exists to show, the gate says so.
        await SeedArchitectureReportAsync(CoverageOnlyYaml);

        var gate = await new AnalysisRepository(_db).GetRunGateAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Error, gate.Status);
        Assert.Null(gate.Grade!.OverallGrade);
    }

    // Run #2958: a rejected grading rule is an F with its reason, not an operational error; it blocks through the
    // minimum grade, and a minimum grade that is itself rejected cannot let that F pass.
    [Theory]
    [InlineData("C")]
    [InlineData("Z")]
    public async Task ARejectedGradingRule_IsAnFThatBlocks_NotAnError(string minimumGrade)
    {
        await SeedArchitectureReportAsync(ArchitectureWithRejectedRuleYaml.Replace("{minimum}", minimumGrade, StringComparison.Ordinal));

        var gate = await new AnalysisRepository(_db).GetRunGateAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Blocked, gate.Status);
        Assert.Equal(AnalysisGrade.F, gate.Grade!.OverallGrade);
        var measures = gate.Grade.Domains.SelectMany(domain => domain.Measures).ToList();
        Assert.Equal(AnalysisGrade.B, measures.Single(measure => measure.Key == "grade.architecture.medium").Grade);
        Assert.Contains(measures, measure => measure.Key == "grade.coverage.line" && measure.Rejection is not null);
    }

    // The coverage rule stops after B, which only a rule on findings may do.
    private const string ArchitectureWithRejectedRuleYaml = """
        stages:
          - name: Grade quality
            steps:
              - name: Grade
                type: analysis-gate
                analysis_scope: quality
                analysis_grading:
                  version: 1
                  minimum_grade: {minimum}
                  required_domains: [architecture]
                  rules:
                    - key: grade.coverage.line
                      domain: reliability
                      metric: coverage.line.percent
                      category: coverage
                      direction: higher-is-better
                      a: 75
                      b: 65
                    - key: grade.architecture.medium
                      domain: architecture
                      severity: medium
                      category: architecture
                      direction: lower-is-better
                      a: 0
                      b: 5
                      c: 15
                      d: 30
                      e: 60
        """;
}
