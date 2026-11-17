// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>The application <see cref="IAppMonitoringService.EnsureCollectionAppAsync"/> settled on, and whether that call created it.</summary>
public sealed record EnsuredCollectionApp(int AppId, bool Created);

public interface IAppMonitoringService
{
    Task<List<MonitoredAppDto>> GetAppsForProjectAsync(int projectId, CancellationToken ct = default);
    Task<MonitoredAppDto?> GetAppAsync(int id, CancellationToken ct = default);
    Task<int?> GetAppProjectIdAsync(int id, CancellationToken ct = default);
    Task<MonitoredAppDto> CreateAppAsync(int projectId, CreateMonitoredAppRequest request, CancellationToken ct = default);
    /// <summary>R-47: configures the telemetry of a project for the pipeline wizard, through the monitored application the
    /// deployment already reads (the one the project has, else one named after the project). Idempotent. Null when the
    /// project does not exist.</summary>
    Task<AppTelemetrySetupDto?> ConfigureTelemetrySetupAsync(int projectId, CancellationToken ct = default);

    /// <summary>R-26: the monitored application the collection of a project goes through, the enabled one the project has
    /// (marked for the telemetry when <paramref name="telemetry"/>), else one created enabled and named after the project.
    /// Idempotent. Null when the project does not exist.</summary>
    Task<EnsuredCollectionApp?> EnsureCollectionAppAsync(int projectId, bool telemetry, CancellationToken ct = default);

    /// <summary>R-47: whether the telemetry the wizard configured still waits for the repository's telemetry package.</summary>
    Task<AppTelemetrySetupDto> GetTelemetrySetupAsync(int projectId, CancellationToken ct = default);

    Task<MonitoredAppDto?> UpdateAppAsync(int id, UpdateMonitoredAppRequest request, CancellationToken ct = default);
    /// <summary>
    /// The server whose agent would probe a new target (another server, or another URL on the same
    /// server) if <paramref name="request"/> were applied; null when no agent target changes or the app
    /// does not exist. Choosing what a server's agent probes needs Write on that server.
    /// </summary>
    Task<int?> GetProbeServerChangeAsync(int id, UpdateMonitoredAppRequest request, CancellationToken ct = default);
    Task<bool> DeleteAppAsync(int id, CancellationToken ct = default);
    Task<List<AppHealthSampleDto>> GetSamplesAsync(int id, int hours, CancellationToken ct = default);

    Task<List<AppProbeConfigDto>> GetProbeConfigsForServerAsync(int serverId, CancellationToken ct = default);

    /// <summary>Maps each app id to its owning ServerId (null = off-fleet) for the agent ownership guard.</summary>
    Task<Dictionary<int, int?>> GetAppServerIdsAsync(IReadOnlyCollection<int> appIds, CancellationToken ct = default);

    /// <summary>
    /// Applies a batch of probe results through the anti-flapping state machine, persists the raw samples,
    /// and fires notifications + SignalR on status transitions. Returns the number of results applied.
    /// Caller MUST have already authorized the batch (agent ownership guard / backend prober owns its apps).
    /// </summary>
    Task<int> IngestProbeResultsAsync(IReadOnlyCollection<AppProbeResultDto> results, CancellationToken ct = default);

    Task<AppMonitoringSummaryDto> GetSummaryAsync(List<int>? accessibleProjectIds, CancellationToken ct = default);
}
