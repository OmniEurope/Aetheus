// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
namespace Aetheus.Back.Components.AppMonitoring;

public sealed class AppDataRetentionRepository(AppDbContext db) : IAppDataRetentionRepository
{
    public async Task<IReadOnlyList<AppDataRetention>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await ToRows(db.MonitoredApps.AsNoTracking()).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(row => new AppDataRetention(
            row.AppId, row.ProjectId, DataRetentionProfiles.Resolve(row.Profile, row.Overrides)))];
    }

    public async Task<DataRetentionValues?> GetForAppAsync(int appId, CancellationToken ct = default)
    {
        var row = await ToRows(db.MonitoredApps.AsNoTracking().Where(app => app.Id == appId)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : DataRetentionProfiles.Resolve(row.Profile, row.Overrides);
    }

    public async Task<AppDataRetentionSetting?> GetSettingForAppAsync(int appId, CancellationToken ct = default)
    {
        var row = await ToRows(db.MonitoredApps.AsNoTracking().Where(app => app.Id == appId)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : new AppDataRetentionSetting(row.Profile, row.Overrides);
    }

    private static IQueryable<Row> ToRows(IQueryable<MonitoredApp> apps) => apps
        .Select(app => new Row(app.Id, app.ProjectId, app.Project.DataRetentionProfile, new DataRetentionOverrides
        {
            TelemetryExportIntervalMinutes = app.Project.TelemetryExportIntervalMinutes,
            TelemetryDetailRetentionDays = app.Project.TelemetryDetailRetentionDays,
            TelemetryAggregateRetentionMonths = app.Project.TelemetryAggregateRetentionMonths,
            AnalysisDetailRetentionDays = app.Project.AnalysisDetailRetentionDays,
            AppLogRetentionDays = app.Project.AppLogRetentionDays
        }));

    private sealed record Row(int AppId, int ProjectId, DataRetentionProfile Profile, DataRetentionOverrides Overrides);
}
