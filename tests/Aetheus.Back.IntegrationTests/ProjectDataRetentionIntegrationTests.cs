// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.IntegrationTests;

/// <summary>On PostgreSQL, Light and Standard projects retain telemetry, logs and analysis for
/// their distinct configured windows; Light ingestion reports its longer send interval.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ProjectDataRetentionIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task EachProject_KeepsItsDataForTheDurationsOfItsProfile()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var light = await SeedProjectAsync("light", DataRetentionProfile.Light, ct);
        var standard = await SeedProjectAsync("standard", DataRetentionProfile.Standard, ct);

        await using (var db = NewContext())
        {
            await AppDataRetentionSweep.PurgeAsync(
                new AppDataRetentionRepository(db), new AppMetricRepository(db), new AppLogRepository(db), Now, ct);
            await new AnalysisDetailRetentionService(
                new AnalysisDetailRetentionRepository(db),
                NullLogger<AnalysisDetailRetentionService>.Instance).PurgeAsync(Now, ct);
        }

        await using var check = NewContext();
        // Telemetry detail 3 days old, logs 10 days old, analysis detail 10 days old.
        Assert.Equal(0, await check.AppMetricSamples.CountAsync(s => s.MonitoredAppId == light.AppId, ct));
        Assert.Equal(1, await check.AppMetricSamples.CountAsync(s => s.MonitoredAppId == standard.AppId, ct));
        Assert.Equal(0, await check.AppLogEntries.CountAsync(l => l.MonitoredAppId == light.AppId, ct));
        Assert.Equal(1, await check.AppLogEntries.CountAsync(l => l.MonitoredAppId == standard.AppId, ct));
        Assert.Equal(0, await DetailAsync(check, light.OldReportId, ct));
        Assert.Equal(4, await DetailAsync(check, standard.OldReportId, ct));
        // Totals stay whatever the profile.
        Assert.Equal(1, await check.AnalysisMetrics.CountAsync(m => m.AnalysisReportId == light.OldReportId && m.FilePath == null, ct));

        var configuration = await new IngestConfigurationService(new AppDataRetentionRepository(check)).GetAsync(light.AppId, ct);
        Assert.Equal(15 * 60, configuration!.MetricExportIntervalSeconds);
    }

    private static Task<int> DetailAsync(AppDbContext db, int reportId, CancellationToken ct) =>
        db.AnalysisMetrics.CountAsync(m => m.AnalysisReportId == reportId && m.FilePath != null, ct);

    private sealed record Seeded(int AppId, int OldReportId);

    private async Task<Seeded> SeedProjectAsync(string name, DataRetentionProfile profile, CancellationToken ct)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = name, Slug = $"{name}-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = name, OrganizationId = organization.Id, DataRetentionProfile = profile };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        var app = new MonitoredApp { ProjectId = project.Id, Name = name };
        db.MonitoredApps.Add(app);
        await db.SaveChangesAsync(ct);
        db.AppMetricSamples.Add(new AppMetricSample { MonitoredAppId = app.Id, MetricName = "cpu", Timestamp = Now.AddDays(-3), Value = 1 });
        db.AppLogEntries.Add(new AppLogEntry { MonitoredAppId = app.Id, Timestamp = Now.AddDays(-10), Body = "old" });
        var old = Report(organization.Id, project.Id, "old");
        var newer = Report(organization.Id, project.Id, "newer");
        db.AnalysisReports.AddRange(old, newer);
        await db.SaveChangesAsync(ct);
        // CreatedAt is stamped by the context on insert: the old report is moved back 10 days.
        await db.AnalysisReports.Where(r => r.Id == old.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.CreatedAt, Now.AddDays(-10)), ct);
        await db.AnalysisReports.Where(r => r.Id == newer.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.CreatedAt, Now.AddDays(-1)), ct);
        db.AnalysisMetrics.AddRange(Enumerable.Range(0, 4).Select(index => new AnalysisMetric
        {
            OrganizationId = organization.Id,
            ProjectId = project.Id,
            AnalysisReportId = old.Id,
            Key = "complexity.file",
            Value = index,
            FilePath = $"src/File{index}.cs",
            ToolName = "roslyn"
        }));
        db.AnalysisMetrics.Add(new AnalysisMetric
        {
            OrganizationId = organization.Id,
            ProjectId = project.Id,
            AnalysisReportId = old.Id,
            Key = "complexity.cyclomatic.maximum",
            Value = 4,
            Scope = "project",
            ToolName = "roslyn"
        });
        await db.SaveChangesAsync(ct);
        return new Seeded(app.Id, old.Id);
    }

    private static AnalysisReport Report(int organizationId, int projectId, string hash) => new()
    {
        OrganizationId = organizationId,
        ProjectId = projectId,
        ScannerKey = "quality",
        ScannerName = "quality",
        ScannerVersion = "1",
        Category = AnalysisCategory.CodeQuality,
        Status = AnalysisReportStatus.Passed,
        Format = AnalysisReportFormat.MetricsJson,
        ContentHash = $"{hash}-{Guid.NewGuid():N}",
        BranchName = "main",
        StartedAt = Now,
        CompletedAt = Now
    };
}
