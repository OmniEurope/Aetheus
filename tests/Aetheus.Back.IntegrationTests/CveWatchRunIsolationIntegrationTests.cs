// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Aetheus.Shared.Components.Analysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// PLAN-004, lot 3, point 10: the findings of the <c>cve-watch</c> scanner belong to no run, so they must not
/// move the gate or the grade of one. Two projects get the same run report through the real ingestion on
/// PostgreSQL; one of them also holds open Critical and High findings of the watch (that a blocking policy would
/// stop if they counted). Gate, counts and grade are read through the real run gate and the real grade reader.
/// </summary>
public sealed class CveWatchRunIsolationIntegrationTests(PostgresFixture fixture) : CveWatchTestBase(fixture)
{
    private sealed record Observation(
        AnalysisGateStatus Gate, int Blockers, int Warnings, int Findings, int NewFindings, int Reports,
        AnalysisGrade? Overall, AnalysisGrade? Minimum, AnalysisGradeCompleteness Completeness, string Domains,
        AnalysisGateStatus? ReportGate, AnalysisGrade? ReportGrade);

    // The security scope is graded on the weighted count of the findings of the run's own reports.
    private const string GradedYaml = """
        name: security
        trigger: manual
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

    private static string Sarif(string level) => """
        {"runs":[{"tool":{"driver":{"name":"OpenGrep"}},"results":[{
          "ruleId":"security.rule","level":"LEVEL","message":{"text":"unsafe"},
          "locations":[{"physicalLocation":{"artifactLocation":{"uri":"src/App.cs"},"region":{"startLine":10}}}]
        }]}]}
        """.Replace("LEVEL", level, StringComparison.Ordinal);

    [Theory]
    [InlineData("note", AnalysisGateStatus.Passed, 0)]
    [InlineData("error", AnalysisGateStatus.Blocked, 1)]
    public async Task AProjectWithOpenWatchFindings_GetsTheSameGateAndGrade_AsOneWithout(string level, AnalysisGateStatus expectedGate, int expectedBlockers)
    {
        await ResetAndMigrateAsync();
        var watched = await SeedProjectAsync("Watched");
        var plain = await SeedProjectAsync("Plain");
        await AddItemAsync(watched, "liba", "1.0.0");
        await AddItemAsync(watched, "libb", "1.0.0");
        await ApplyAsync(
            Advisory("GHSA-1", "LibA", UpToFix),
            Advisory("GHSA-2", "LibB", UpToFix, severity: "CRITICAL", cve: "CVE-2099-0002"));
        await PassAsync();
        var watchFindings = await OpenWatchFindingsAsync(watched.ProjectId);
        Assert.Equal([AnalysisSeverity.High, AnalysisSeverity.Critical], watchFindings.Select(finding => finding.Severity).Order());
        Assert.Empty(await OpenWatchFindingsAsync(plain.ProjectId));
        await BlockHighFindingsAsync(watched);
        await BlockHighFindingsAsync(plain);

        var watchedRun = await PublishRunReportAsync(watched, level);
        var plainRun = await PublishRunReportAsync(plain, level);

        var withWatch = await ObserveAsync(watched.ProjectId, watchedRun);
        var without = await ObserveAsync(plain.ProjectId, plainRun);
        // The setup does gate: a blocking policy blocks a High finding of the run, and lets a Low one through.
        Assert.Equal(expectedGate, without.Gate);
        Assert.Equal(expectedBlockers, without.Blockers);
        Assert.Equal(without, withWatch);
        // The findings of the watch are still there, open, and were not touched by the ingestion of the run.
        Assert.Equal(2, (await OpenWatchFindingsAsync(watched.ProjectId)).Count);

        // And a watch that publishes after the run changes nothing of what the run already got.
        await ApplyAsync(Advisory("GHSA-3", "LibC", UpToFix, severity: "CRITICAL", cve: "CVE-2099-0003"));
        await AddItemAsync(plain, "libc", "1.0.0");
        await PassAsync();
        Assert.Single(await OpenWatchFindingsAsync(plain.ProjectId));
        Assert.Equal(without, await ObserveAsync(plain.ProjectId, plainRun));
    }

    // Each project was evaluated at its own moment; that moment is the only thing allowed to differ.
    private static string WithoutEvaluationTime(string domainsJson) =>
        Regex.Replace(domainsJson, "\"EvaluatedAt\":\"[^\"]*\"", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(1));

    private async Task<List<AnalysisFinding>> OpenWatchFindingsAsync(int projectId)
    {
        await using var db = NewContext();
        return await db.AnalysisFindings.AsNoTracking()
            .Where(finding => finding.ProjectId == projectId && finding.Status == AnalysisFindingStatus.Open
                && finding.Occurrences.Any(occurrence => occurrence.ScannerKey == CveWatchFindings.ScannerKey))
            .ToListAsync(Ct);
    }

    private async Task BlockHighFindingsAsync((int OrganizationId, int ProjectId, int InventoryId) owner)
    {
        await using var db = NewContext();
        db.AnalysisPolicies.Add(new AnalysisPolicy
        {
            OrganizationId = owner.OrganizationId,
            ProjectId = owner.ProjectId,
            PolicyKey = "test.block.high",
            Name = "Block High",
            SeverityThreshold = AnalysisSeverity.High,
            Behavior = AnalysisGateBehavior.Block,
            Priority = 1000,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A run of the project, and one report of it through the real ingestion. Returns the run.</summary>
    private async Task<(int RunId, AnalysisReportDto Report)> PublishRunReportAsync(
        (int OrganizationId, int ProjectId, int InventoryId) owner, string level)
    {
        int runId;
        int artifactId;
        await using (var db = NewContext())
        {
            var pipeline = new Pipeline { Name = "Security", ProjectId = owner.ProjectId, YamlDefinition = GradedYaml };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(Ct);
            var run = new PipelineRun
            {
                PipelineId = pipeline.Id,
                Status = PipelineStatus.Running,
                StartedAt = DateTime.UtcNow,
                BranchName = "feature/isolation",
                CommitHash = new string('a', 40)
            };
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(Ct);
            var artifact = new PipelineArtifact { PipelineId = pipeline.Id, PipelineRunId = run.Id, Name = "analysis-opengrep", FilePath = "analysis-opengrep.zip" };
            db.PipelineArtifacts.Add(artifact);
            await db.SaveChangesAsync(Ct);
            (runId, artifactId) = (run.Id, artifact.Id);
        }

        await using var context = NewContext();
        var repository = new AnalysisRepository(context);
        var runtime = Options.Create(new AnalysisPlatformOptions());
        var service = new AnalysisService(
            repository, new AnalysisIngestGate(runtime), new AnalysisPolicyEngine(repository), TimeProvider.System,
            Substitute.For<IAuditService>(), new DbTransactionScope(context), Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<Aetheus.Back.Components.Notifications.INotificationService>(), Substitute.For<IDependencyTrackOutbox>(),
            runtime, Options.Create(new DependencyTrackOptions()));
        var report = await service.PublishReportAsync(runId, new PublishAnalysisReportRequest
        {
            ScannerKey = "opengrep",
            ScannerName = "OpenGrep",
            ScannerVersion = "1.22.0",
            Category = AnalysisCategory.Sast,
            Status = AnalysisReportStatus.Failed,
            Format = AnalysisReportFormat.Sarif,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            CompletedAt = DateTime.UtcNow,
            ReportPath = "report.sarif",
            PipelineArtifactId = artifactId,
            ReportContent = Sarif(level)
        }, Ct);
        return (runId, report);
    }

    private async Task<Observation> ObserveAsync(int projectId, (int RunId, AnalysisReportDto Report) run)
    {
        await using var db = NewContext();
        var repository = new AnalysisRepository(db);
        var runtime = Options.Create(new AnalysisPlatformOptions());
        var service = new AnalysisService(
            repository, new AnalysisIngestGate(runtime), new AnalysisPolicyEngine(repository), TimeProvider.System,
            Substitute.For<IAuditService>(), new DbTransactionScope(db), Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<Aetheus.Back.Components.Notifications.INotificationService>(), Substitute.For<IDependencyTrackOutbox>(),
            runtime, Options.Create(new DependencyTrackOptions()));
        var gate = await service.GetRunGateAsync(run.RunId, Ct);
        // The analysis grades as measured: this test isolates runs, not the deployed release qualification (R-12, R-20).
        var grade = (await new AnalysisProjectSummaryRepository(db).GetAnalysisGradesAsync([projectId], new Dictionary<int, int>(), Ct)).GetValueOrDefault(projectId);
        Assert.NotNull(grade);
        return new Observation(
            gate.Status, gate.BlockerCount, gate.WarningCount, gate.FindingCount, gate.NewFindingCount, gate.ReportCount,
            grade.OverallGrade, grade.MinimumGrade, grade.Completeness, WithoutEvaluationTime(JsonSerializer.Serialize(grade.Domains)),
            run.Report.GateStatus, run.Report.Grade);
    }
}
