// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
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

/// <summary>Publishes a large report twice on PostgreSQL and records timings while checking
/// baseline resolution. Retention keeps totals, worst rows and threshold violations while
/// preserving full-report grading.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalysisMetricIngestionIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private const int MetricCount = 39_240;

    /// <summary>Values cycle from 1 to 97; the project policy warns above 95.</summary>
    private const double WarnAbove = 95;

    [Fact]
    public async Task ArchitectureSizedMetricsReport_WritesTenTimesFewerRowsWithTheSameGradeAndGate()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var pipelineId = await SeedPipelineAsync(ct);

        var firstRun = await SeedRunAsync(pipelineId, ct);
        var first = await PublishTimedAsync(firstRun, "first", ct);
        await using (var db = NewContext())
        {
            await db.PipelineRuns.Where(run => run.Id == firstRun.RunId)
                .ExecuteUpdateAsync(set => set.SetProperty(run => run.Status, PipelineStatus.Success), ct);
        }
        var secondRun = await SeedRunAsync(pipelineId, ct);
        var second = await PublishTimedAsync(secondRun, "second (with baseline)", ct);

        await using var verification = NewContext();
        var stored = await verification.AnalysisMetrics.CountAsync(m => m.AnalysisReportId == first.Id, ct);
        TestContext.Current.TestOutputHelper?.WriteLine($"Rows written: {stored} of {MetricCount}");
        Assert.True(stored * 10 <= MetricCount, $"{stored} rows written out of {MetricCount}");
        var overThreshold = Values().Count(value => value > WarnAbove);
        Assert.Equal(overThreshold, await verification.AnalysisMetrics
            .CountAsync(m => m.AnalysisReportId == first.Id && m.Value > WarnAbove, ct));
        Assert.Equal(stored, await verification.AnalysisMetrics.CountAsync(m => m.AnalysisReportId == second.Id, ct));
        Assert.Equal(stored, await verification.AnalysisMetrics
            .CountAsync(m => m.AnalysisReportId == second.Id && m.BaselineValue != null, ct));

        // The Gate counted every row of the report, not only the written ones.
        var evaluation = await verification.AnalysisEvaluations.AsNoTracking().SingleAsync(e => e.AnalysisReportId == first.Id, ct);
        Assert.Equal(AnalysisGateStatus.Warning, evaluation.Status);
        Assert.Equal(overThreshold, evaluation.WarningCount);
        // The grade read back from the written rows is the one computed on the whole report.
        Assert.NotNull(evaluation.Grade);
        var report = await verification.AnalysisReports.AsNoTracking().Include(r => r.Metrics).SingleAsync(r => r.Id == first.Id, ct);
        var context = await new AnalysisRepository(verification).GetRunContextAsync(firstRun.RunId, ct);
        // Recomputed at the instant the snapshot carries: PostgreSQL keeps EvaluatedAt to the microsecond.
        var instant = Summary(evaluation.GradeSnapshotJson).EvaluatedAt!.Value;
        Assert.Equal(evaluation.GradeSnapshotHash, AnalysisGradeEngine.Evaluate(report, context!, instant).SnapshotHash);
    }

    private static AnalysisGradeSummaryDto Summary(string json) =>
        JsonSerializer.Deserialize<AnalysisGradeSummaryDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static IEnumerable<double> Values() => Enumerable.Range(0, MetricCount).Select(index => (double)(index % 97 + 1));

    private async Task<AnalysisReportDto> PublishTimedAsync((int RunId, int ArtifactId) run, string label, CancellationToken ct)
    {
        await using var db = NewContext();
        var service = CreateService(db);
        var stopwatch = Stopwatch.StartNew();
        var report = await service.PublishReportAsync(run.RunId, Request(run.ArtifactId), ct);
        stopwatch.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Ingestion {label}: {MetricCount} metrics in {stopwatch.Elapsed.TotalSeconds:F2} s");
        return report;
    }

    private static PublishAnalysisReportRequest Request(int artifactId) => new()
    {
        ScannerKey = "dotnet-architecture-metrics",
        ScannerName = "Microsoft.CodeAnalysis Architecture",
        ScannerVersion = "4.14.0",
        Category = AnalysisCategory.Architecture,
        Status = AnalysisReportStatus.Passed,
        Format = AnalysisReportFormat.MetricsJson,
        StartedAt = DateTime.UtcNow.AddMinutes(-1),
        CompletedAt = DateTime.UtcNow,
        ReportPath = "dotnet-architecture-metrics.json",
        PipelineArtifactId = artifactId,
        ReportContent = JsonSerializer.Serialize(new
        {
            // The same shape ArchitectureAnalysisPublisher emits: one record per dependency edge.
            metrics = Enumerable.Range(0, MetricCount).Select(index => new
            {
                key = "architecture.dependencies.namespace",
                value = (double)(index % 97 + 1),
                unit = "edge",
                scope = "namespace-edge",
                language = "csharp",
                filePath = $"src/Aetheus.Back/Components/Module{index % 400}/File{index}.cs",
                symbol = $"Aetheus.Back.Components.Module{index % 400}->Aetheus.Shared.Target{index}",
                toolName = "Microsoft.CodeAnalysis Architecture",
                direction = "LowerIsBetter"
            }).Append(new
            {
                key = "architecture.cycles",
                value = 0.0,
                unit = "cycles",
                scope = "project",
                language = "csharp",
                filePath = (string)null!,
                symbol = (string)null!,
                toolName = "Microsoft.CodeAnalysis Architecture",
                direction = "LowerIsBetter"
            })
        })
    };

    private async Task<int> SeedPipelineAsync(CancellationToken ct)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = "Ingestion", Slug = $"ingestion-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = "Project", OrganizationId = organization.Id, DefaultBranch = "main" };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        var pipeline = new Pipeline { Name = "Quality", ProjectId = project.Id, YamlDefinition = GradedYaml };
        db.Pipelines.Add(pipeline);
        db.AnalysisPolicies.Add(new AnalysisPolicy
        {
            OrganizationId = organization.Id,
            ProjectId = project.Id,
            PolicyKey = "test.dependencies",
            Name = "Too many references",
            MetricKey = "architecture.dependencies.namespace",
            Operator = AnalysisPolicyOperator.GreaterThan,
            Threshold = WarnAbove,
            Behavior = AnalysisGateBehavior.Warn,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return pipeline.Id;
    }

    private const string GradedYaml = """
        name: quality
        trigger: manual
        stages:
          - name: Quality
            steps:
              - name: Grade
                type: analysis-gate
                analysis_scope: quality
                analysis_grading:
                  version: 1
                  minimum_grade: F
                  required_domains: [architecture]
                  rules:
                    - key: grade.dependencies
                      domain: architecture
                      metric: architecture.dependencies.namespace
                      category: architecture
                      direction: lower-is-better
                      a: 10
                      b: 20
                      c: 40
                      d: 60
                      e: 90
        """;

    private async Task<(int RunId, int ArtifactId)> SeedRunAsync(int pipelineId, CancellationToken ct)
    {
        await using var db = NewContext();
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
            Name = "analysis-dotnet-architecture-metrics",
            FilePath = "dotnet-architecture-metrics.json"
        };
        db.PipelineArtifacts.Add(artifact);
        await db.SaveChangesAsync(ct);
        return (run.Id, artifact.Id);
    }

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
