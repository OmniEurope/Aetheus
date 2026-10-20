// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Notifications;

namespace Aetheus.Back.Components.Projects;

/// <summary>Estimate project storage from detail-row counts and measured or fallback row sizes;
/// alert followers once at 80% of the budget and, when exceeded, purge oldest telemetry, logs, then
/// eligible analysis details down to 90%. Preserve grades, totals, hourly aggregates, each scanner
/// latest report and retained-release reports.</summary>
public sealed class ProjectStorageBudgetService(
    IProjectStorageRepository repository,
    IAnalysisDetailRetentionRepository analysisDetail,
    IAppTelemetryService telemetry,
    INotificationService notifications,
    TimeProvider timeProvider,
    ILogger<ProjectStorageBudgetService> logger) : IProjectStorageBudgetService
{
    internal const int BatchSize = 5_000;

    /// <summary>Delete statements per sweep, all projects together; the next sweep continues.</summary>
    internal const int MaxBatchesPerSweep = 200;

    /// <summary>How old a database size measurement may be to give the bytes per row.</summary>
    internal static readonly TimeSpan MeasurementMaxAge = TimeSpan.FromDays(2);

    public async Task<ProjectStorageReportDto> GetReportAsync(
        IReadOnlyCollection<int>? accessibleProjectIds, CancellationToken ct = default)
    {
        var state = await ComputeAsync(ct).ConfigureAwait(false);
        return new ProjectStorageReportDto
        {
            MeasuredAt = state.MeasuredAt,
            Projects = [.. state.Usages.Where(usage => accessibleProjectIds is null || accessibleProjectIds.Contains(usage.ProjectId))]
        };
    }

    public async Task<ProjectStorageSweep> EnforceAsync(CancellationToken ct = default)
    {
        var state = await ComputeAsync(ct).ConfigureAwait(false);
        var alertLevels = state.Budgets.ToDictionary(budget => budget.ProjectId, budget => budget.AlertLevel);
        var sweep = new ProjectStorageSweep();
        foreach (var usage in state.Usages)
        {
            await AlertAsync(usage, alertLevels[usage.ProjectId], ct).ConfigureAwait(false);
            if (usage.UsedBytes <= usage.BudgetBytes) continue;
            sweep.ProjectsOverBudget++;
            var excess = usage.UsedBytes - usage.BudgetBytes * ProjectStorageDefaults.PurgeTargetPercent / 100;
            await PurgeOldestAsync(usage.ProjectId, excess, state.BytesPerRow, sweep, ct).ConfigureAwait(false);
        }
        if (sweep.Rows > 0)
            logger.LogInformation(
                "Project storage budget: {Projects} projects over budget, {Rows} detail rows deleted in {Batches} batches",
                sweep.ProjectsOverBudget, sweep.Rows, sweep.Batches);
        return sweep;
    }

    private async Task<UsageState> ComputeAsync(CancellationToken ct)
    {
        var measurement = await FindOwnMeasurementAsync(ct).ConfigureAwait(false);
        var budgets = await repository.GetBudgetsAsync(ct).ConfigureAwait(false);
        var rows = await repository.CountRowsAsync(ct).ConfigureAwait(false);
        var bytesPerRow = ProjectStorageTables.BytesPerRow(measurement);
        return new UsageState(measurement?.MeasuredAt, budgets, bytesPerRow, ProjectStorageUsage.Build(budgets, rows, bytesPerRow));
    }

    private sealed record UsageState(
        DateTime? MeasuredAt,
        IReadOnlyList<ProjectStorageBudget> Budgets,
        IReadOnlyDictionary<string, double> BytesPerRow,
        List<ProjectStorageUsageDto> Usages);

    /// <summary>The database size measurement of the Aetheus database itself: the application whose
    /// measured database carries the name Aetheus is connected to.</summary>
    private async Task<AppDatabaseSizeReportDto?> FindOwnMeasurementAsync(CancellationToken ct)
    {
        var name = repository.DatabaseName;
        if (string.IsNullOrEmpty(name)) return null;
        var since = timeProvider.GetUtcNow().UtcDateTime - MeasurementMaxAge;
        foreach (var appId in await repository.GetAppsMeasuringADatabaseAsync(since, ct).ConfigureAwait(false))
        {
            var report = await telemetry.GetDatabaseSizeAsync(appId, ct).ConfigureAwait(false);
            if (string.Equals(report.DatabaseName, name, StringComparison.Ordinal)) return report;
        }
        return null;
    }

    private async Task AlertAsync(ProjectStorageUsageDto usage, int previousLevel, CancellationToken ct)
    {
        var level = ProjectStorageUsage.AlertLevel(usage.UsedPercent);
        if (level == previousLevel || (level > 0 && level < previousLevel)) return;
        await repository.SetAlertLevelAsync(usage.ProjectId, level, ct).ConfigureAwait(false);
        if (level == 0) return;
        await notifications.SendEventAsync("project.storage.quota", new
        {
            usage.ProjectId,
            UsagePercent = usage.UsedPercent,
            Threshold = level,
            usage.UsedBytes,
            usage.BudgetBytes
        }, ct).ConfigureAwait(false);
    }

    private async Task PurgeOldestAsync(
        int projectId, long excessBytes, IReadOnlyDictionary<string, double> bytesPerRow,
        ProjectStorageSweep sweep, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        while (excessBytes > 0 && sweep.Batches < MaxBatchesPerSweep)
        {
            var (table, deleted) = await DeleteOldestBatchAsync(projectId, now, ct).ConfigureAwait(false);
            if (deleted == 0) break;
            sweep.Batches++;
            sweep.Rows += deleted;
            excessBytes -= (long)Math.Round(deleted * ProjectStorageUsage.BytesOf(bytesPerRow, table));
        }
    }

    /// <summary>Deletes one batch of whichever detail of the project is the oldest.</summary>
    private async Task<(string Table, int Deleted)> DeleteOldestBatchAsync(int projectId, DateTime now, CancellationToken ct)
    {
        var sample = await repository.GetOldestSampleAsync(projectId, ct).ConfigureAwait(false);
        var log = await repository.GetOldestLogAsync(projectId, ct).ConfigureAwait(false);
        var report = await analysisDetail.GetOldestPurgeableReportDateAsync(projectId, now, ct).ConfigureAwait(false);
        var oldest = new[] { sample, log, report }.Where(value => value.HasValue).Min();
        if (oldest is null) return (string.Empty, 0);
        if (oldest == sample)
            return (ProjectStorageTables.MetricSamples,
                await repository.DeleteOldestSamplesAsync(projectId, BatchSize, ct).ConfigureAwait(false));
        if (oldest == log)
            return (ProjectStorageTables.Logs,
                await repository.DeleteOldestLogsAsync(projectId, BatchSize, ct).ConfigureAwait(false));
        var reportIds = await analysisDetail.GetReportsWithExpiredDetailAsync(now, [projectId], 1, ct).ConfigureAwait(false);
        var metrics = await analysisDetail.DeleteMetricDetailBatchAsync(reportIds, BatchSize, ct).ConfigureAwait(false);
        if (metrics > 0) return (ProjectStorageTables.AnalysisMetrics, metrics);
        return (ProjectStorageTables.AnalysisComponents,
            await analysisDetail.DeleteComponentBatchAsync(reportIds, BatchSize, ct).ConfigureAwait(false));
    }
}

/// <summary>What one budget sweep did.</summary>
public sealed class ProjectStorageSweep
{
    public int ProjectsOverBudget { get; set; }
    public long Rows { get; set; }
    public int Batches { get; set; }
}
