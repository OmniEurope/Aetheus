// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AppMonitoring;

public sealed class AppTelemetrySendIntervalService(
    IAppDataRetentionRepository retention,
    IAppMetricRepository metrics,
    TimeProvider timeProvider) : IAppTelemetrySendIntervalService
{
    /// <summary>
    /// How many server intervals of samples are read: enough for the last sends at the server's interval
    /// or at any shorter one, which covers an old package (60 seconds by default, never longer than the
    /// shortest interval a project may set). Reading only that window keeps the query to the recent rows.
    /// </summary>
    internal const int LookbackIntervals = TelemetrySendIntervalObserver.RecentGaps + 2;

    /// <summary>Distinct instants read: a send usually carries one, so this covers the recent sends.</summary>
    internal const int MaximumInstants = 500;

    public async Task<AppTelemetrySendIntervalDto?> GetAsync(int appId, CancellationToken ct = default)
    {
        var setting = await retention.GetSettingForAppAsync(appId, ct).ConfigureAwait(false);
        if (setting is null)
            return null;

        var values = DataRetentionProfiles.Resolve(setting.Profile, setting.Overrides);
        var since = timeProvider.GetUtcNow().UtcDateTime
            - TimeSpan.FromMinutes(values.TelemetryExportIntervalMinutes * LookbackIntervals);
        var instants = await metrics.GetRecentSampleInstantsAsync(appId, since, MaximumInstants, ct).ConfigureAwait(false);
        return new AppTelemetrySendIntervalDto
        {
            ServerIntervalSeconds = values.TelemetryExportIntervalMinutes * 60,
            Profile = setting.Profile,
            IsProjectOverride = setting.Overrides.TelemetryExportIntervalMinutes.HasValue,
            ObservedIntervalSeconds = TelemetrySendIntervalObserver.ObserveSeconds(instants)
        };
    }
}
