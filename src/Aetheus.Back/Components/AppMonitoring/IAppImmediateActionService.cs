// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AppMonitoring;

/// <summary> R-29: the two actions a user triggers from the Supervision instead of waiting for the next period, an
/// immediate availability probe and an immediate telemetry export. The caller has authorized the user on the
/// application's project. </summary>
public interface IAppImmediateActionService
{
    /// <summary>Probes now: during the call for a backend-probed application, at the agent's next configuration read for
    /// an agent-probed one. Null when the application does not exist; <see cref="ConflictException"/> when it cannot be
    /// probed (monitoring disabled, no probe URL).</summary>
    Task<AppProbeNowDto?> ProbeNowAsync(int appId, CancellationToken ct = default);

    Task<AppProbeNowDto?> GetProbeNowAsync(int appId, CancellationToken ct = default);

    /// <summary>Asks the application's telemetry package to export now. Null when the application does not exist;
    /// <see cref="ConflictException"/> when it has no ingest key (no package can take the request).</summary>
    Task<AppTelemetryExportRequestDto?> RequestTelemetryExportAsync(int appId, CancellationToken ct = default);

    Task<AppTelemetryExportRequestDto?> GetTelemetryExportRequestAsync(int appId, CancellationToken ct = default);

    /// <summary>Hands the pending export request over to the package polling with the application's ingest key, once;
    /// its identifier, or null when none is pending.</summary>
    Task<string?> TakeTelemetryExportRequestAsync(int appId, CancellationToken ct = default);
}
