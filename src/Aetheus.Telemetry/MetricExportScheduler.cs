// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;

namespace Aetheus.Telemetry;

/// <summary> Sends the metrics at the interval of <see cref="MetricExportSchedule"/>, which follows
/// the « Conservation des données » setting of the application's project (1, 5 or 15 minutes by
/// profile). The interval is read from Aetheus at startup and every <see cref="RefreshPeriod"/>, so
/// a change applies without a restart. R-29: between two exports it asks Aetheus every <see
/// cref="ExportRequestPollPeriod"/> whether a user requested an export from the Supervision, and
/// sends the metrics at once when one is pending. </summary>
internal sealed class MetricExportScheduler(
    MetricReader reader,
    MetricExportSchedule schedule,
    ServerConfigurationClient client,
    TimeProvider timeProvider,
    ILogger<MetricExportScheduler>? logger = null) : BackgroundService
{
    internal static readonly TimeSpan RefreshPeriod = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan ExportRequestPollPeriod = TimeSpan.FromSeconds(30);

    private DateTimeOffset _nextRefresh = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // R-23: the first export leaves at startup, right after the interval is read, instead of one full
        // interval later (up to 15 minutes), so the metrics resume as soon as the application is restarted
        // or redeployed.
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshIfDueAsync(stoppingToken).ConfigureAwait(false);
            if (!first)
                await WaitForNextExportAsync(stoppingToken).ConfigureAwait(false);
            first = false;
            reader.Collect();
        }
    }

    /// <summary>Waits for the periodic export, answering the export requests of Aetheus meanwhile.</summary>
    private async Task WaitForNextExportAsync(CancellationToken ct)
    {
        var due = timeProvider.GetUtcNow() + schedule.Interval;
        while (due - timeProvider.GetUtcNow() is var remaining && remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining < ExportRequestPollPeriod ? remaining : ExportRequestPollPeriod, timeProvider, ct)
                .ConfigureAwait(false);
            if (timeProvider.GetUtcNow() < due)
                await ExportIfRequestedAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Sends the metrics now when Aetheus holds an export request; true when it did.</summary>
    internal async Task<bool> ExportIfRequestedAsync(CancellationToken ct)
    {
        var requestId = await client.ReadExportRequestAsync(ct).ConfigureAwait(false);
        if (requestId is null)
            return false;
        var exported = reader.Collect();
        logger?.LogInformation(
            "Aetheus telemetry: metrics sent at the request {RequestId} of Aetheus (exported: {Exported})", requestId, exported);
        return true;
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
