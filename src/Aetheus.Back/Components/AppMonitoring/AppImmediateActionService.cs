// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>
/// R-29. The immediate probe of a backend-probed application runs during the call and goes through the regular
/// ingestion (state machine, samples, notifications); an agent-probed one is carried in the agent's probe configuration
/// and answered by the first sample the agent reports after it. The immediate export travels on the configuration
/// channel the <c>Aetheus.Telemetry</c> package polls; it is answered by the first metrics export that arrives after
/// the package took it. A result is never an older reading: no sample, export or visit from before the request counts.
/// </summary>
public sealed class AppImmediateActionService(
    IAppMonitoringRepository repo,
    IAppMonitoringService monitoring,
    AppHttpProber prober,
    TimeProvider timeProvider) : IAppImmediateActionService
{
    public async Task<AppProbeNowDto?> ProbeNowAsync(int appId, CancellationToken ct = default)
    {
        var app = await repo.GetAppForUpdateAsync(appId, ct).ConfigureAwait(false);
        if (app is null)
            return null;
        if (!app.Enabled)
            throw new ConflictException("Monitoring is disabled for this application: enable it before probing.");
        if (string.IsNullOrWhiteSpace(app.ProbeUrl))
            throw new ConflictException("This application has no probe URL.");

        var now = Now();
        // A second click while the first request is still fresh returns it instead of probing again.
        if (app.ProbeRequestedAt is { } previous && (now - previous).TotalSeconds < AppImmediateActionDefaults.DebounceSeconds)
            return await DescribeProbeAsync(app, now, ct).ConfigureAwait(false);

        app.ProbeRequestedAt = now;
        app.ProbeRequestResultAt = null;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        if (app.ServerId is null)
        {
            var result = await prober.ProbeAsync(app.Id, app.ProbeUrl, app.ExpectedStatusCode, app.ProbeTimeoutSeconds, ct)
                .ConfigureAwait(false);
            await monitoring.IngestProbeResultsAsync([result], ct).ConfigureAwait(false);
        }

        return await DescribeProbeAsync(app, Now(), ct).ConfigureAwait(false);
    }

    public async Task<AppProbeNowDto?> GetProbeNowAsync(int appId, CancellationToken ct = default)
    {
        var app = await repo.GetAppForUpdateAsync(appId, ct).ConfigureAwait(false);
        return app is null ? null : await DescribeProbeAsync(app, Now(), ct).ConfigureAwait(false);
    }

    public async Task<AppTelemetryExportRequestDto?> RequestTelemetryExportAsync(int appId, CancellationToken ct = default)
    {
        var app = await repo.GetAppForUpdateAsync(appId, ct).ConfigureAwait(false);
        if (app is null)
            return null;
        if (app.IngestKeyHash is null)
            throw new ConflictException("This application has no ingest key: no telemetry package can take the request.");

        var now = Now();
        var current = DescribeExport(app, now);
        // A request still under way (or answered a few seconds ago) is returned instead of a new one.
        var underWay = current.RequestedAt is not null && current.ReceivedAt is null && !current.TimedOut;
        var justAnswered = current.ReceivedAt is { } received
            && (now - received).TotalSeconds < AppImmediateActionDefaults.DebounceSeconds;
        if (underWay || justAnswered)
            return current;

        app.TelemetryExportRequestedAt = now;
        app.TelemetryExportDeliveredAt = null;
        app.TelemetryExportReceivedAt = null;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        return DescribeExport(app, now);
    }

    public async Task<AppTelemetryExportRequestDto?> GetTelemetryExportRequestAsync(int appId, CancellationToken ct = default)
    {
        var app = await repo.GetAppForUpdateAsync(appId, ct).ConfigureAwait(false);
        return app is null ? null : DescribeExport(app, Now());
    }

    public async Task<string?> TakeTelemetryExportRequestAsync(int appId, CancellationToken ct = default)
    {
        var requestedAt = await repo.TakeTelemetryExportRequestAsync(appId, Now(), ct).ConfigureAwait(false);
        return requestedAt?.ToString("O", CultureInfo.InvariantCulture);
    }

    private async Task<AppProbeNowDto> DescribeProbeAsync(MonitoredApp app, DateTime now, CancellationToken ct)
    {
        AppHealthSampleDto? result = null;
        if (app.ProbeRequestResultAt is { } answeredAt)
        {
            // PostgreSQL keeps microseconds, the tracked entity keeps ticks: one millisecond of slack finds the sample.
            var samples = await repo.GetSamplesSinceAsync(app.Id, answeredAt.AddMilliseconds(-1), ct).ConfigureAwait(false);
            result = samples
                .Select(sample => new AppHealthSampleDto
                {
                    Timestamp = sample.Timestamp,
                    IsUp = sample.IsUp,
                    ResponseTimeMs = sample.ResponseTimeMs,
                    StatusCode = sample.StatusCode,
                    Error = sample.Error
                })
                .FirstOrDefault();
        }

        return new AppProbeNowDto
        {
            RequestedAt = app.ProbeRequestedAt,
            ByAgent = app.ServerId is not null,
            Result = result,
            TimedOut = app.ProbeRequestedAt is { } requestedAt && app.ProbeRequestResultAt is null
                && (now - requestedAt).TotalSeconds > AppImmediateActionDefaults.AgentProbeWaitSeconds
        };
    }

    private static AppTelemetryExportRequestDto DescribeExport(MonitoredApp app, DateTime now)
    {
        var requestedAt = app.TelemetryExportRequestedAt;
        var deliveredAt = app.TelemetryExportDeliveredAt;
        var receivedAt = deliveredAt is not null && app.TelemetryExportReceivedAt >= deliveredAt
            ? app.TelemetryExportReceivedAt
            : null;
        var timedOut = requestedAt is { } asked && receivedAt is null && (deliveredAt is { } taken
            ? (now - taken).TotalSeconds > AppImmediateActionDefaults.TelemetryExportWaitSeconds
            : (now - asked).TotalSeconds > AppImmediateActionDefaults.TelemetryPickupWaitSeconds);
        return new AppTelemetryExportRequestDto
        {
            RequestedAt = requestedAt,
            DeliveredAt = deliveredAt,
            ReceivedAt = receivedAt,
            TimedOut = timedOut,
            AnalyticsEnabled = app.AnalyticsEnabled,
            VisitsReceivedAt = requestedAt is not null && app.AnalyticsLastIngestAt >= requestedAt
                ? app.AnalyticsLastIngestAt
                : null,
            LastIngestAt = app.LastIngestAt,
            DataReceivedAt = requestedAt is not null && app.LastIngestAt >= requestedAt ? app.LastIngestAt : null
        };
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;
}
