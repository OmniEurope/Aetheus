// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// PLAN-013 lot 5 control on PostgreSQL: a project pushed over its budget (10 MB) is back under it after
/// one sweep, its oldest telemetry gone first; its grade, its totals and its newest analysis report stay,
/// and a project under its budget is not touched.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProjectStorageBudgetIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private const int Samples = 30_000; // 30,000 x 480 bytes = 13.7 MB, over a 10 MB budget

    [Fact]
    public async Task AProjectOverItsBudget_IsBackUnderIt_WithoutLosingItsGrade()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var over = await SeedAsync("over", budgetMegabytes: 10, ct);
        var under = await SeedAsync("under", budgetMegabytes: null, ct);

        ProjectStorageSweep sweep;
        await using (var db = NewContext())
            sweep = await Service(db).EnforceAsync(ct);

        await using var check = NewContext();
        var report = await Service(check).GetReportAsync(null, ct);
        var overUsage = report.Projects.Single(project => project.ProjectId == over.ProjectId);
        Assert.Equal(1, sweep.ProjectsOverBudget);
        Assert.True(overUsage.UsedBytes <= overUsage.BudgetBytes, $"{overUsage.UsedBytes} bytes used for a budget of {overUsage.BudgetBytes}");
        // The oldest samples went first: the newest one is still there.
        Assert.True(await check.AppMetricSamples.AnyAsync(s => s.MonitoredAppId == over.AppId && s.Timestamp == Now.AddMinutes(-1), ct));
        Assert.False(await check.AppMetricSamples.AnyAsync(s => s.MonitoredAppId == over.AppId && s.Timestamp == Now.AddMinutes(-Samples), ct));
        // Grade, totals and the newest report of the scanner stay.
        Assert.Equal(AnalysisGrade.B, (await check.AnalysisEvaluations.SingleAsync(e => e.ProjectId == over.ProjectId, ct)).Grade);
        Assert.Equal(1, await check.AnalysisMetrics.CountAsync(m => m.ProjectId == over.ProjectId && m.FilePath == null, ct));
        Assert.Equal(1, await check.AnalysisMetrics.CountAsync(m => m.ProjectId == over.ProjectId && m.FilePath != null, ct));
        // The project under its budget kept everything.
        Assert.Equal(Samples, await check.AppMetricSamples.CountAsync(s => s.MonitoredAppId == under.AppId, ct));
        Assert.Equal(100, (await check.Projects.SingleAsync(p => p.Id == over.ProjectId, ct)).StorageQuotaAlertLevel);
    }

    private static ProjectStorageBudgetService Service(AppDbContext db) => new(
        new ProjectStorageRepository(db),
        new AnalysisDetailRetentionRepository(db),
        Substitute.For<IAppTelemetryService>(),
        Substitute.For<INotificationService>(),
        new FixedTime(Now),
        NullLogger<ProjectStorageBudgetService>.Instance);

    private sealed record Seeded(int ProjectId, int AppId);

    private async Task<Seeded> SeedAsync(string name, int? budgetMegabytes, CancellationToken ct)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = name, Slug = $"{name}-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = name, OrganizationId = organization.Id, StorageBudgetMegabytes = budgetMegabytes };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        var app = new MonitoredApp { ProjectId = project.Id, Name = name };
        db.MonitoredApps.Add(app);
        await db.SaveChangesAsync(ct);
        db.AppMetricSamples.AddRange(Enumerable.Range(1, Samples).Select(minutes => new AppMetricSample
        {
            MonitoredAppId = app.Id,
            MetricName = "cpu",
            Timestamp = Now.AddMinutes(-minutes),
            Value = minutes
        }));
        var analysisReport = new AnalysisReport
        {
            OrganizationId = organization.Id,
            ProjectId = project.Id,
            ScannerKey = "quality",
            ScannerName = "quality",
            ScannerVersion = "1",
            Category = AnalysisCategory.CodeQuality,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.MetricsJson,
            ContentHash = Guid.NewGuid().ToString("N"),
            BranchName = "main",
            StartedAt = Now,
            CompletedAt = Now
        };
        db.AnalysisReports.Add(analysisReport);
        await db.SaveChangesAsync(ct);
        db.AnalysisMetrics.AddRange(
            new AnalysisMetric { OrganizationId = organization.Id, ProjectId = project.Id, AnalysisReportId = analysisReport.Id, Key = "complexity.file", Value = 9, FilePath = "src/A.cs", ToolName = "roslyn" },
            new AnalysisMetric { OrganizationId = organization.Id, ProjectId = project.Id, AnalysisReportId = analysisReport.Id, Key = "complexity.cyclomatic.maximum", Value = 9, Scope = "project", ToolName = "roslyn" });
        db.AnalysisEvaluations.Add(new AnalysisEvaluation
        {
            OrganizationId = organization.Id,
            ProjectId = project.Id,
            AnalysisReportId = analysisReport.Id,
            Status = AnalysisGateStatus.Passed,
            Grade = AnalysisGrade.B,
            EvaluatedAt = Now
        });
        await db.SaveChangesAsync(ct);
        return new Seeded(project.Id, app.Id);
    }

    private sealed class FixedTime(DateTime at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(at);
    }
}
