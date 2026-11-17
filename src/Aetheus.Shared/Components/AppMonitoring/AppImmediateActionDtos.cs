// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.AppMonitoring;

/// <summary> R-29: where the last immediate availability probe a user asked for stands. A backend-probed
/// application is probed during the request, so <see cref="Result"/> comes back at once; an agent-probed
/// application is probed by its server's agent at its next configuration read, so the result arrives later.
/// <see cref="Result"/> is the first probe sample taken at or after <see cref="RequestedAt"/>, never an older
/// one. </summary>
public sealed record AppProbeNowDto
{
    /// <summary>When the probe was asked for; null when no immediate probe was ever asked for.</summary>
    public DateTime? RequestedAt { get; init; }

    /// <summary>True when the server's agent runs the probe (the result arrives at its next report).</summary>
    public bool ByAgent { get; init; }

    public AppHealthSampleDto? Result { get; init; }

    /// <summary>True when no result arrived within <see cref="AppImmediateActionDefaults.AgentProbeWaitSeconds"/>.</summary>
    public bool TimedOut { get; init; }
}

/// <summary> R-29: where the last immediate telemetry export a user asked for stands. The request travels on the
/// configuration channel the application's <c>Aetheus.Telemetry</c> package polls with its ingest key:
/// <see cref="DeliveredAt"/> is when the package took it, <see cref="ReceivedAt"/> when the metrics export it sent in
/// answer arrived. The audience is not buffered by the application (the web analytics events leave it within seconds
/// of each visit), so <see cref="VisitsReceivedAt"/> is the latest audience event received since the request, or null
/// when no visit arrived since. </summary>
public sealed record AppTelemetryExportRequestDto
{
    public DateTime? RequestedAt { get; init; }
    public DateTime? DeliveredAt { get; init; }
    public DateTime? ReceivedAt { get; init; }

    /// <summary>True when the package did not take the request within
    /// <see cref="AppImmediateActionDefaults.TelemetryPickupWaitSeconds"/> (a package older than 1.2.2, or the
    /// application is stopped), or the export did not arrive within
    /// <see cref="AppImmediateActionDefaults.TelemetryExportWaitSeconds"/> after it took it.</summary>
    public bool TimedOut { get; init; }

    public bool AnalyticsEnabled { get; init; }
    public DateTime? VisitsReceivedAt { get; init; }

    /// <summary>The last telemetry of any kind (metrics, logs, errors) Aetheus received from the application, whenever it
    /// came; null when none ever arrived (no key used yet).</summary>
    public DateTime? LastIngestAt { get; init; }

    /// <summary>The same reading, only when it is at or after <see cref="RequestedAt"/>: data that really reached Aetheus
    /// since the test, whether the package answered the request or sent its periodic export. Null when nothing arrived
    /// since.</summary>
    public DateTime? DataReceivedAt { get; init; }
}

/// <summary>R-29: how long the immediate actions wait before saying their result did not arrive.</summary>
public static class AppImmediateActionDefaults
{
    /// <summary>The agent reads its probe configuration every minute and probes on its 15 s tick.</summary>
    public const int AgentProbeWaitSeconds = 180;

    /// <summary>The telemetry package polls for export requests every 30 seconds.</summary>
    public const int TelemetryPickupWaitSeconds = 120;

    public const int TelemetryExportWaitSeconds = 60;

    /// <summary>A second click within this delay returns the action already under way instead of a new one.</summary>
    public const int DebounceSeconds = 10;
}
