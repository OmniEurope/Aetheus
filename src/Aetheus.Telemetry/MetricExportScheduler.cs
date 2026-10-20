// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;

namespace Aetheus.Telemetry;

/// <summary> Sends the metrics at the interval of <see cref="MetricExportSchedule"/>, which follows
/// the « Conservation des données » setting of the application's project (1, 5 or 15 minutes by
/// profile). The interval is read from Aetheus at startup and every <see cref="RefreshPeriod"/>, so
/// a change applies without a restart. </summary>
internal sealed class MetricExportScheduler(
    MetricReader reader,
    MetricExportSchedule schedule,
    ServerConfigurationClient client,
    TimeProvider timeProvider,
    ILogger<MetricExportScheduler>? logger = null) : BackgroundService
{
    internal static readonly TimeSpan RefreshPeriod = TimeSpan.FromMinutes(15);

    private DateTimeOffset _nextRefresh = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshIfDueAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(schedule.Interval, timeProvider, stoppingToken).ConfigureAwait(false);
            reader.Collect();
        }
    }

    /// <summary>Reads the interval from Aetheus when the refresh period has elapsed.</summary>
    internal async Task RefreshIfDueAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        if (now < _nextRefresh)
            return;
        _nextRefresh = now + RefreshPeriod;
        var seconds = await client.ReadMetricExportIntervalSecondsAsync(ct).ConfigureAwait(false);
        if (seconds is null)
            return;
        var before = schedule.Interval;
        schedule.Apply(seconds.Value);
        if (schedule.Interval != before)
            logger?.LogInformation("Aetheus telemetry: metrics are now sent every {Seconds} s", schedule.Interval.TotalSeconds);
    }
}
