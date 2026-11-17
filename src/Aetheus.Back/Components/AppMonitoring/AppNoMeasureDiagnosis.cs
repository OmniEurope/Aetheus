// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>
/// R-27: why a server measure (response times, database size) never arrived, read from what the server already holds about the
/// application: its ingestion key (created, valid, expired), its enabled flag, the key refusal log and the last reception. The
/// Aetheus.Telemetry package's own configuration is not readable from here, so the last two causes are said as what they are:
/// the possible reasons, not a verified one.
/// </summary>
public static class AppNoMeasureDiagnosis
{
    public static AppNoMeasureDto Build(MonitoredApp app, DateTime now, IngestKeyRejectionLog rejections)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(rejections);

        if (app.IngestKeyHash is null)
            return Of(app, AppNoMeasureCause.NoIngestKey);
        if (!app.Enabled)
            return Of(app, AppNoMeasureCause.AppDisabled);
        // The server's own gate (ResolveIngestKeyHashAsync) accepts a key only while its expiry is set and not past.
        if (app.IngestKeyExpiresAt is null || app.IngestKeyExpiresAt < now)
            return Of(app, AppNoMeasureCause.KeyExpired, app.IngestKeyExpiresAt);

        var refused = rejections.LastRefusalOf(
            [app.IngestKeyHash, app.PreviousIngestKeyHash, app.SecondPreviousIngestKeyHash]);
        if (refused is { } refusedAt)
            return Of(app, AppNoMeasureCause.KeyRefused, refusedAt);

        return Of(app, app.LastIngestAt is null ? AppNoMeasureCause.NothingReceived : AppNoMeasureCause.MeasureMissing);
    }

    private static AppNoMeasureDto Of(MonitoredApp app, AppNoMeasureCause cause, DateTime? causeAt = null) => new()
    {
        Cause = cause,
        CauseAt = causeAt,
        LastReceptionAt = app.LastIngestAt
    };
}
