// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppMonitoring;

public readonly record struct IngestKeyResolution(int AppId, DateTime? ValidUntilUtc);
public readonly record struct AppUptimeWindowCounts(
    int Up24h,
    int Total24h,
    int Up7d,
    int Total7d,
    int Up90d,
    int Total90d);

/// <summary>Recette R-441: the fleet server whose enabled Apache virtual host serves a probe URL host.</summary>
public readonly record struct AppHostingServer(int ServerId, string ServerName);

public interface IAppMonitoringRepository
{
    /// <summary>
    /// Recette R-441: maps each lower-case host name to the one fleet server of the project's
    /// organization that serves it through an enabled Apache virtual host. A host served by several
    /// servers, or by none, is left out: the hosting server is then unknown rather than guessed.
    /// </summary>
    Task<Dictionary<string, AppHostingServer>> GetVirtualHostServersAsync(
        int projectId,
        IReadOnlyCollection<string> hostNames,
        CancellationToken ct = default);

    Task<List<MonitoredApp>> GetAppsByProjectAsync(int projectId, CancellationToken ct = default);
    Task<MonitoredApp?> GetAppAsync(int id, CancellationToken ct = default);
    Task<MonitoredApp?> GetAppForUpdateAsync(int id, CancellationToken ct = default);
    Task<MonitoredApp?> GetAppByAnalyticsSiteIdAsync(string siteId, CancellationToken ct = default);
    Task<List<MonitoredApp>> GetAnalyticsKeyRotationCandidatesAsync(
        DateTime createdBeforeUtc,
        int maxCount,
        CancellationToken ct = default);
    Task<List<MonitoredApp>> GetAnalyticsConfiguredAppsPageAsync(
        int afterId,
        int maxCount,
        CancellationToken ct = default);
    Task<int?> GetAppProjectIdAsync(int id, CancellationToken ct = default);
    /// <summary>The organization of a server; null when the server does not exist.</summary>
    Task<int?> GetServerOrganizationIdAsync(int serverId, CancellationToken ct = default);
    /// <summary>True when the environment exists and is the project's own or a shared one (no project).</summary>
    Task<bool> EnvironmentIsUsableByProjectAsync(int environmentId, int projectId, CancellationToken ct = default);

    /// <summary>Maps each project id to its OrganizationId (for per-org SignalR broadcast).</summary>
    Task<Dictionary<int, int>> GetProjectOrgIdsAsync(IReadOnlyCollection<int> projectIds, CancellationToken ct = default);

    /// <summary>Apps monitored by black-box backend probing (off-fleet: ServerId is null, URL set, enabled).</summary>
    Task<List<MonitoredApp>> GetBackendProbedAppsAsync(CancellationToken ct = default);

    /// <summary>Enabled probe configs for the apps hosted by a given fleet server.</summary>
    Task<List<MonitoredApp>> GetAppsForServerAsync(int serverId, CancellationToken ct = default);

    /// <summary>Maps each app id to its owning ServerId (null when off-fleet) for the agent ownership guard.</summary>
    Task<Dictionary<int, int?>> GetAppServerIdsAsync(IReadOnlyCollection<int> appIds, CancellationToken ct = default);

    Task<Dictionary<int, MonitoredApp>> GetAppsByIdsForUpdateAsync(IReadOnlyCollection<int> appIds, CancellationToken ct = default);

    /// <summary>Status histogram across a set of accessible projects (null = all) for the dashboard tile.</summary>
    Task<List<MonitoredApp>> GetAppsForSummaryAsync(List<int>? accessibleProjectIds, CancellationToken ct = default);
    /// <summary>Distinct active web sessions per monitored application after the supplied cutoff.</summary>
    Task<Dictionary<int, int>> GetActiveVisitorCountsAsync(
        IReadOnlyCollection<int> appIds,
        DateTime activeAfterUtc,
        CancellationToken ct = default);
    Task<long> GetTelemetryStorageBytesAsync(CancellationToken ct = default);

    /// <summary>Recette R-468: the audience aggregates of the current day, week and month, added up over
    /// the given applications; one entry per period that has any.</summary>
    Task<List<AppAudiencePeriodTotal>> GetAudienceTotalsAsync(
        IReadOnlyCollection<int> appIds,
        DateOnly todayUtc,
        DateOnly weekStartUtc,
        DateOnly monthStartUtc,
        CancellationToken ct = default);

    Task AddAppAsync(MonitoredApp app, CancellationToken ct = default);
    Task RemoveAppAsync(MonitoredApp app, CancellationToken ct = default);
    Task AddSamplesAsync(IEnumerable<AppHealthSample> samples, CancellationToken ct = default);
    Task<List<AppHealthSample>> GetSamplesSinceAsync(int appId, DateTime since, CancellationToken ct = default);

    /// <summary>Raw uptime (up, total) from samples since a cutoff.</summary>
    Task<(int Up, int Total)> GetRawUptimeAsync(int appId, DateTime since, CancellationToken ct = default);

    /// <summary>Aggregated uptime (up, total) from hourly rollups since a cutoff.</summary>
    Task<(int Up, int Total)> GetHourlyUptimeAsync(int appId, DateTime since, CancellationToken ct = default);

    /// <summary>
    /// Returns uptime windows for all requested apps in two grouped queries: raw 24h plus hourly
    /// rollups for 7d/90d, with the unaggregated current-hour raw tail added to both rollup windows.
    /// Missing ids have no samples and are omitted.
    /// </summary>
    Task<Dictionary<int, AppUptimeWindowCounts>> GetUptimeWindowsAsync(
        IReadOnlyCollection<int> appIds,
        DateTime nowUtc,
        CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);

    // --- OTLP ingestion (phase 2+) ---
    /// <summary>Resolves an ingest key hash to its enabled app id (null if unknown/disabled).</summary>
    Task<IngestKeyResolution?> ResolveIngestKeyHashAsync(
        string ingestKeyHash,
        DateTime nowUtc,
        CancellationToken ct = default);
    /// <summary>Records a successful ingest touch and increments the dropped counter (surfaced in UI).</summary>
    Task TouchIngestAsync(int appId, long droppedDelta, DateTime now, CancellationToken ct = default);

    /// <summary>Tracked enabled app to auto-instrument on deploy: prefers an environment match, then a
    /// project-level (null environment) app. Null when the project has no enabled monitored app.</summary>
    Task<MonitoredApp?> GetDeployTargetAppAsync(int projectId, int? environmentId, CancellationToken ct = default);

    // --- retention ---
    /// <summary>R2-020: rolls up one completed hour, application by application; a no-op for the rolled-up ones.</summary>
    Task<int> AggregateHourAsync(DateTime hourUtc, CancellationToken ct = default);
    Task<int> PurgeRawOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
    Task<int> PurgeHourlyOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}
