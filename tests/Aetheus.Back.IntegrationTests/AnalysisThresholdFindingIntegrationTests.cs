// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>R1-36 / ADR-053 on PostgreSQL, through the real ingestion: a coverage under its threshold is a
/// finding, the same finding from one run to the next, accepted it stops warning, fixed when the coverage is
/// back over the threshold, and open again when it falls under it again.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalysisThresholdFindingIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private const string RuleId = "threshold.metric.quality.coverage.line";

    [Fact]
    public async Task R136_ACoverageUnderItsThreshold_IsATrackedFinding_ThatCanBeDecidedAndFixed()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var pipelineId = await SeedPipelineAsync(ct);

        // Run 1: 70 % is under the 80 % default. One finding, one warning, counted once.
        var first = await PublishAsync(pipelineId, 70, ct);
        var evaluation = await EvaluationOfAsync(first.Id, ct);
        Assert.Equal((AnalysisGateStatus.Warning, 1, 0), (evaluation.Status, evaluation.WarningCount, evaluation.BlockerCount));
        var finding = await SingleThresholdFindingAsync(ct);
        Assert.Equal(AnalysisFindingStatus.Open, finding.Status);
        Assert.Equal(AnalysisSeverity.Medium, finding.Severity);
        Assert.Equal(AnalysisCategory.Coverage, finding.Category);
        Assert.Equal(RuleId, finding.RuleId);

        // Run 2: 65 %, worse. The same finding, a second occurrence, no second finding.
        await PublishAsync(pipelineId, 65, ct);
        var again = await SingleThresholdFindingAsync(ct);
        Assert.Equal(finding.Id, again.Id);
        await using (var db = NewContext())
            Assert.Equal(2, await db.AnalysisFindingOccurrences.CountAsync(item => item.AnalysisFindingId == finding.Id, ct));

        // Accepted: the rule no longer warns for it.
        await SetStatusAsync(finding.Id, AnalysisFindingStatus.Accepted, ct);
        var accepted = await PublishAsync(pipelineId, 60, ct);
        var acceptedEvaluation = await EvaluationOfAsync(accepted.Id, ct);
        Assert.Equal((AnalysisGateStatus.Passed, 0), (acceptedEvaluation.Status, acceptedEvaluation.WarningCount));
        Assert.Equal(AnalysisFindingStatus.Accepted, (await SingleThresholdFindingAsync(ct)).Status);

        // Back over the threshold: the finding is fixed, not deleted.
        await PublishAsync(pipelineId, 91, ct);
        var fixedFinding = await SingleThresholdFindingAsync(ct);
        Assert.Equal(finding.Id, fixedFinding.Id);
        Assert.Equal(AnalysisFindingStatus.Fixed, fixedFinding.Status);
        Assert.NotNull(fixedFinding.ResolvedAt);

        // Under it again: the same finding, open again, and the rule warns once more.
        var regressed = await PublishAsync(pipelineId, 50, ct);
        var reopened = await SingleThresholdFindingAsync(ct);
        Assert.Equal(finding.Id, reopened.Id);
        Assert.Equal(AnalysisFindingStatus.Open, reopened.Status);
        Assert.Equal(1, (await EvaluationOfAsync(regressed.Id, ct)).WarningCount);
    }

    private async Task<AnalysisFinding> SingleThresholdFindingAsync(CancellationToken ct)
    {
        await using var db = NewContext();
        return await db.AnalysisFindings.AsNoTracking().SingleAsync(item => item.RuleId.StartsWith("threshold.metric."), ct);
    }

    private async Task<AnalysisEvaluation> EvaluationOfAsync(int reportId, CancellationToken ct)
    {
        await using var db = NewContext();
        return await db.AnalysisEvaluations.AsNoTracking().SingleAsync(item => item.AnalysisReportId == reportId, ct);
    }

    private async Task SetStatusAsync(int findingId, AnalysisFindingStatus status, CancellationToken ct)
    {
        await using var db = NewContext();
        await db.AnalysisFindings.Where(item => item.Id == findingId)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, status), ct);
    }

    private async Task<AnalysisReportDto> PublishAsync(int pipelineId, double coverage, CancellationToken ct)
    {
        int runId;
        int artifactId;
        await using (var db = NewContext())
        {
            var run = new PipelineRun
            {
                PipelineId = pipelineId,
                Status = PipelineStatus.Running,
                StartedAt = DateTime.UtcNow,
                BranchName = "main",
                CommitHash = new string('a', 40)
            };
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync(ct);
            var artifact = new PipelineArtifact
            {
                PipelineId = pipelineId,
                PipelineRunId = run.Id,
                Name = "analysis-coverlet",
                FilePath = "coverage-metrics.json"
            };
            db.PipelineArtifacts.Add(artifact);
            await db.SaveChangesAsync(ct);
            (runId, artifactId) = (run.Id, artifact.Id);
        }

        await using var publish = NewContext();
        return await CreateService(publish).PublishReportAsync(runId, new PublishAnalysisReportRequest
        {
            ScannerKey = "coverlet",
            ScannerName = "Coverlet",
            ScannerVersion = "1",
            Category = AnalysisCategory.Coverage,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.MetricsJson,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            CompletedAt = DateTime.UtcNow,
            ReportPath = "coverage-metrics.json",
            StepName = "Back",
            PipelineArtifactId = artifactId,
            ReportContent = JsonSerializer.Serialize(new
            {
                metrics = new[]
                {
                    new
                    {
                        key = "coverage.line.percent",
                        value = coverage,
                        unit = "percent",
                        scope = "project",
                        language = "csharp",
                        toolName = "Coverlet",
                        direction = "HigherIsBetter"
                    }
                }
            })
        }, ct);
    }

    private async Task<int> SeedPipelineAsync(CancellationToken ct)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = "Thresholds", Slug = $"thresholds-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = "Project", OrganizationId = organization.Id, DefaultBranch = "main" };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        var pipeline = new Pipeline { Name = "Quality", ProjectId = project.Id, YamlDefinition = Yaml };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);
        return pipeline.Id;
    }

    private const string Yaml = """
        name: quality
        trigger: manual
        stages:
          - name: Quality
            steps:
              - name: Gate
                type: analysis-gate
                analysis_scope: quality
        """;

    private static AnalysisService CreateService(AppDbContext db)
    {
        var repository = new AnalysisRepository(db);
        var runtime = Options.Create(new AnalysisPlatformOptions());
        return new AnalysisService(
            repository,
            new AnalysisIngestGate(runtime),
            new AnalysisPolicyEngine(repository),
            TimeProvider.System,
            Substitute.For<IAuditService>(),
            new DbTransactionScope(db),
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<INotificationService>(),
            Substitute.For<IDependencyTrackOutbox>(),
            runtime,
            Options.Create(new DependencyTrackOptions()));
    }
}
