// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppMonitoring;

public sealed class AppMonitoringService(
    IAppMonitoringRepository repo,
    INotificationService notificationService,
    IEntityChangeNotifier notifier,
    TimeProvider timeProvider,
    ILogger<AppMonitoringService> logger) : IAppMonitoringService
{
    public async Task<List<MonitoredAppDto>> GetAppsForProjectAsync(int projectId, CancellationToken ct = default)
    {
        var apps = await repo.GetAppsByProjectAsync(projectId, ct).ConfigureAwait(false);
        if (apps.Count == 0)
            return [];

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var uptimes = await repo.GetUptimeWindowsAsync(
            apps.Select(app => app.Id).ToList(), now, ct).ConfigureAwait(false);
        var hostingServers = await GetHostingServersAsync(projectId, apps, ct).ConfigureAwait(false);
        return apps.Select(app => MapWithUptime(
            app,
            uptimes.GetValueOrDefault(app.Id),
            now) with
        { HostingServer = HostingServerFor(app, hostingServers) }).ToList();
    }

    public async Task<MonitoredAppDto?> GetAppAsync(int id, CancellationToken ct = default)
    {
        var app = await repo.GetAppAsync(id, ct).ConfigureAwait(false);
        if (app is null) return null;
        var dto = await MapWithUptimeAsync(app, ct).ConfigureAwait(false);
        var hostingServers = await GetHostingServersAsync(app.ProjectId, [app], ct).ConfigureAwait(false);
        return dto with { HostingServer = HostingServerFor(app, hostingServers) };
    }

    /// <summary> An app registered without a server is probed by the backend, but it usually still
    /// runs on a fleet server. That server is read from the Apache virtual host serving the probe
    /// URL's host, for display only: the probe keeps running from the backend. </summary>
    private async Task<Dictionary<string, AppHostingServer>> GetHostingServersAsync(
        int projectId,
        IEnumerable<MonitoredApp> apps,
        CancellationToken ct)
    {
        var hosts = apps
            .Where(app => app.ServerId is null)
            .Select(app => ProbeHost(app.ProbeUrl))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return hosts.Count == 0
            ? []
            : await repo.GetVirtualHostServersAsync(projectId, hosts, ct).ConfigureAwait(false);
    }

    private static MonitoredAppHostingServerDto? HostingServerFor(
        MonitoredApp app,
        Dictionary<string, AppHostingServer> hostingServers) =>
        app.ServerId is null
        && ProbeHost(app.ProbeUrl) is { } host
        && hostingServers.TryGetValue(host, out var server)
            ? new MonitoredAppHostingServerDto { ServerId = server.ServerId, ServerName = server.ServerName }
            : null;

    private static string? ProbeHost(string? probeUrl) =>
        Uri.TryCreate(probeUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.IdnHost)
            ? uri.IdnHost.ToLowerInvariant()
            : null;

    public Task<int?> GetAppProjectIdAsync(int id, CancellationToken ct = default) =>
        repo.GetAppProjectIdAsync(id, ct);

    public async Task<MonitoredAppDto> CreateAppAsync(int projectId, CreateMonitoredAppRequest request, CancellationToken ct = default)
    {
        ValidateProbeUrl(request.ProbeUrl);
        await ValidateTargetsAsync(projectId, request.EnvironmentId, request.ServerId, ct).ConfigureAwait(false);

        var app = new MonitoredApp
        {
            ProjectId = projectId,
            EnvironmentId = request.EnvironmentId,
            ServerId = request.ServerId,
            Name = request.Name.Trim(),
            ProbeUrl = string.IsNullOrWhiteSpace(request.ProbeUrl) ? null : request.ProbeUrl.Trim(),
            ProbeIntervalSeconds = Math.Clamp(request.ProbeIntervalSeconds, AppMonitoringDefaults.MinimumProbeIntervalSeconds, AppMonitoringDefaults.MaximumProbeIntervalSeconds),
            ProbeTimeoutSeconds = Math.Clamp(request.ProbeTimeoutSeconds, AppMonitoringDefaults.MinimumProbeTimeoutSeconds, AppMonitoringDefaults.MaximumProbeTimeoutSeconds),
            ExpectedStatusCode = Math.Clamp(request.ExpectedStatusCode, AppMonitoringDefaults.MinimumExpectedStatusCode, AppMonitoringDefaults.MaximumExpectedStatusCode),
            FailureThreshold = Math.Clamp(request.FailureThreshold, AppMonitoringDefaults.MinimumTransitionThreshold, AppMonitoringDefaults.MaximumTransitionThreshold),
            RecoveryThreshold = Math.Clamp(request.RecoveryThreshold, AppMonitoringDefaults.MinimumTransitionThreshold, AppMonitoringDefaults.MaximumTransitionThreshold),
            Enabled = request.Enabled,
            CurrentStatus = AppHealthStatus.Unknown
        };

        await repo.AddAppAsync(app, ct).ConfigureAwait(false);
        await BroadcastAppChangeAsync(app.ProjectId, EntityChangeOps.Created, ct).ConfigureAwait(false);
        return await MapWithUptimeAsync(app, ct).ConfigureAwait(false);
    }

    public async Task<MonitoredAppDto?> UpdateAppAsync(int id, UpdateMonitoredAppRequest request, CancellationToken ct = default)
    {
        ValidateProbeUrl(request.ProbeUrl);

        var app = await repo.GetAppForUpdateAsync(id, ct).ConfigureAwait(false);
        if (app is null)
            return null;
        await ValidateTargetsAsync(app.ProjectId, request.EnvironmentId, request.ServerId, ct).ConfigureAwait(false);

        app.Name = request.Name.Trim();
        app.EnvironmentId = request.EnvironmentId;
        app.ServerId = request.ServerId;
        app.ProbeUrl = string.IsNullOrWhiteSpace(request.ProbeUrl) ? null : request.ProbeUrl.Trim();
        app.ProbeIntervalSeconds = Math.Clamp(request.ProbeIntervalSeconds, AppMonitoringDefaults.MinimumProbeIntervalSeconds, AppMonitoringDefaults.MaximumProbeIntervalSeconds);
        app.ProbeTimeoutSeconds = Math.Clamp(request.ProbeTimeoutSeconds, AppMonitoringDefaults.MinimumProbeTimeoutSeconds, AppMonitoringDefaults.MaximumProbeTimeoutSeconds);
        app.ExpectedStatusCode = Math.Clamp(request.ExpectedStatusCode, AppMonitoringDefaults.MinimumExpectedStatusCode, AppMonitoringDefaults.MaximumExpectedStatusCode);
        app.FailureThreshold = Math.Clamp(request.FailureThreshold, AppMonitoringDefaults.MinimumTransitionThreshold, AppMonitoringDefaults.MaximumTransitionThreshold);
        app.RecoveryThreshold = Math.Clamp(request.RecoveryThreshold, AppMonitoringDefaults.MinimumTransitionThreshold, AppMonitoringDefaults.MaximumTransitionThreshold);
        app.Enabled = request.Enabled;

        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await BroadcastAppChangeAsync(app.ProjectId, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        return await MapWithUptimeAsync(app, ct).ConfigureAwait(false);
    }

    public async Task<int?> GetProbeServerChangeAsync(int id, UpdateMonitoredAppRequest request, CancellationToken ct = default)
    {
        if (request.ServerId is not { } serverId) return null;
        var app = await repo.GetAppAsync(id, ct).ConfigureAwait(false);
        if (app is null) return null;
        var probeUrl = string.IsNullOrWhiteSpace(request.ProbeUrl) ? null : request.ProbeUrl.Trim();
        return app.ServerId != serverId || !string.Equals(app.ProbeUrl, probeUrl, StringComparison.Ordinal)
            ? serverId
            : null;
    }

    // BACK-008: the probing server and the environment must belong to the app's own organization and
    // project, refused before anything is persisted or handed to an agent.
    private async Task ValidateTargetsAsync(int projectId, int? environmentId, int? serverId, CancellationToken ct)
    {
        if (serverId is { } server)
        {
            var projectOrganization = (await repo.GetProjectOrgIdsAsync([projectId], ct).ConfigureAwait(false))
                .GetValueOrDefault(projectId);
            var serverOrganization = await repo.GetServerOrganizationIdAsync(server, ct).ConfigureAwait(false);
            if (serverOrganization is null || serverOrganization != projectOrganization)
                throw new BadRequestException("The probing server must belong to the project's organization.");
        }
        if (environmentId is { } environment
            && !await repo.EnvironmentIsUsableByProjectAsync(environment, projectId, ct).ConfigureAwait(false))
            throw new BadRequestException("The environment does not belong to this project.");
    }

    public async Task<bool> DeleteAppAsync(int id, CancellationToken ct = default)
    {
        var app = await repo.GetAppForUpdateAsync(id, ct).ConfigureAwait(false);
        if (app is null)
            return false;

        var projectId = app.ProjectId;
        await repo.RemoveAppAsync(app, ct).ConfigureAwait(false);
        await BroadcastAppChangeAsync(projectId, EntityChangeOps.Deleted, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<List<AppHealthSampleDto>> GetSamplesAsync(int id, int hours, CancellationToken ct = default)
    {
        var since = timeProvider.GetUtcNow().UtcDateTime.AddHours(-Math.Clamp(hours, AppMonitoringDefaults.MinimumHistoryHours, AppMonitoringDefaults.MaximumHistoryHours));
        var samples = await repo.GetSamplesSinceAsync(id, since, ct).ConfigureAwait(false);
        return samples.Select(s => new AppHealthSampleDto
        {
            Timestamp = s.Timestamp,
            IsUp = s.IsUp,
            ResponseTimeMs = s.ResponseTimeMs,
            StatusCode = s.StatusCode,
            Error = s.Error
        }).ToList();
    }

    public async Task<List<AppProbeConfigDto>> GetProbeConfigsForServerAsync(int serverId, CancellationToken ct = default)
    {
        var apps = await repo.GetAppsForServerAsync(serverId, ct).ConfigureAwait(false);
        return apps.Select(a => new AppProbeConfigDto
        {
            MonitoredAppId = a.Id,
            ProbeUrl = a.ProbeUrl ?? string.Empty,
            ProbeIntervalSeconds = a.ProbeIntervalSeconds,
            ProbeTimeoutSeconds = a.ProbeTimeoutSeconds,
            ExpectedStatusCode = a.ExpectedStatusCode
        }).ToList();
    }

    public Task<Dictionary<int, int?>> GetAppServerIdsAsync(IReadOnlyCollection<int> appIds, CancellationToken ct = default) =>
        repo.GetAppServerIdsAsync(appIds, ct);

    public async Task<int> IngestProbeResultsAsync(IReadOnlyCollection<AppProbeResultDto> results, CancellationToken ct = default)
    {
        if (results.Count == 0)
            return 0;

        var ids = results.Select(r => r.MonitoredAppId).Distinct().ToList();
        var apps = await repo.GetAppsByIdsForUpdateAsync(ids, ct).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var samples = new List<AppHealthSample>(results.Count);
        var transitions = new List<(MonitoredApp App, AppHealthStatus Previous, AppHealthStatus Next)>();

        foreach (var result in results.OrderBy(r => r.Timestamp))
        {
            if (!apps.TryGetValue(result.MonitoredAppId, out var app))
                continue; // unknown app id in the batch: silently skipped (ownership already enforced upstream)

            var ts = result.Timestamp == default
                ? now
                : DateTime.SpecifyKind(result.Timestamp.ToUniversalTime(), DateTimeKind.Utc);

            samples.Add(new AppHealthSample
            {
                MonitoredAppId = app.Id,
                Timestamp = ts,
                IsUp = result.IsUp,
                ResponseTimeMs = result.ResponseTimeMs,
                StatusCode = result.StatusCode,
                Error = Truncate(result.Error, 500)
            });

            var previous = app.CurrentStatus;
            var transition = AppHealthStateMachine.Apply(
                previous, app.ConsecutiveFailures, app.ConsecutiveSuccesses,
                result.IsUp, app.FailureThreshold, app.RecoveryThreshold);

            app.ConsecutiveFailures = transition.ConsecutiveFailures;
            app.ConsecutiveSuccesses = transition.ConsecutiveSuccesses;
            app.LastCheckedAt = ts;
            app.LastResponseTimeMs = result.ResponseTimeMs;

            if (transition.Changed)
            {
                app.CurrentStatus = transition.Status;
                app.LastStatusChangeAt = ts;
                transitions.Add((app, previous, transition.Status));
            }
        }

        // Single save: samples and the tracked app mutations share the same DbContext.
        await repo.AddSamplesAsync(samples, ct).ConfigureAwait(false);

        if (transitions.Count > 0)
            await FanOutTransitionsAsync(transitions, ct).ConfigureAwait(false);

        return samples.Count;
    }

    public async Task<AppMonitoringSummaryDto> GetSummaryAsync(List<int>? accessibleProjectIds, CancellationToken ct = default)
    {
        if (accessibleProjectIds is { Count: 0 })
            return new AppMonitoringSummaryDto();

        var apps = await repo.GetAppsForSummaryAsync(accessibleProjectIds, ct).ConfigureAwait(false);
        var telemetryStorageBytes = await repo.GetTelemetryStorageBytesAsync(ct).ConfigureAwait(false);
        var analyticsAppIds = apps
            .Where(app => app.AnalyticsEnabled)
            .Select(app => app.Id)
            .ToList();
        Dictionary<int, int> activeVisitorCounts = analyticsAppIds.Count == 0
            ? []
            : await repo.GetActiveVisitorCountsAsync(
                analyticsAppIds,
                timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-5),
                ct).ConfigureAwait(false);

        var applications = apps
            .OrderBy(app => app.CurrentStatus == AppHealthStatus.Up)
            .ThenByDescending(app => app.LastStatusChangeAt ?? DateTime.MinValue)
            .ThenBy(app => app.Name)
            .Select(a => new MonitoredAppStatusDto
            {
                Id = a.Id,
                Name = a.Name,
                ProjectId = a.ProjectId,
                ProjectName = a.Project?.Name,
                CurrentStatus = a.CurrentStatus,
                LastStatusChangeAt = a.LastStatusChangeAt,
                OnlineVisitorCount = a.AnalyticsEnabled
                    ? activeVisitorCounts.GetValueOrDefault(a.Id)
                    : null,
                LastEventAt = LatestOf(a.LastIngestAt, a.AnalyticsLastIngestAt)
            })
            .ToList();
        var troubled = applications
            .Where(app => app.CurrentStatus is AppHealthStatus.Down or AppHealthStatus.Degraded)
            .Take(10)
            .ToList();
        var audience = await GetAudienceTotalsAsync(analyticsAppIds, activeVisitorCounts, ct).ConfigureAwait(false);

        return new AppMonitoringSummaryDto
        {
            TotalCount = apps.Count,
            UpCount = apps.Count(a => a.CurrentStatus == AppHealthStatus.Up),
            DownCount = apps.Count(a => a.CurrentStatus == AppHealthStatus.Down),
            DegradedCount = apps.Count(a => a.CurrentStatus == AppHealthStatus.Degraded),
            UnknownCount = apps.Count(a => a.CurrentStatus == AppHealthStatus.Unknown),
            TelemetryStorageBytes = telemetryStorageBytes,
            Applications = applications,
            Troubled = troubled,
            Audience = audience
        };
    }

    /// <summary>The audience of the visible applications added up, from the aggregates the
    /// ingestion already keeps per day, week and month. Null when no application measures its
    /// audience.</summary>
    private async Task<AppAudienceTotalsDto?> GetAudienceTotalsAsync(
        List<int> analyticsAppIds, Dictionary<int, int> activeVisitorCounts, CancellationToken ct)
    {
        if (analyticsAppIds.Count == 0) return null;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var totals = await repo.GetAudienceTotalsAsync(
            analyticsAppIds,
            DateOnly.FromDateTime(now),
            AppWebAnalyticsRepository.WeekStart(now),
            new DateOnly(now.Year, now.Month, 1),
            ct).ConfigureAwait(false);
        var day = totals.FirstOrDefault(total => total.Kind == AnalyticsPeriodKind.Day);
        var week = totals.FirstOrDefault(total => total.Kind == AnalyticsPeriodKind.Week);
        var month = totals.FirstOrDefault(total => total.Kind == AnalyticsPeriodKind.Month);
        return new AppAudienceTotalsDto
        {
            ApplicationCount = analyticsAppIds.Count,
            OnlineVisitors = activeVisitorCounts.Values.Sum(),
            VisitorsToday = day?.UniqueVisitors ?? 0,
            VisitorsThisWeek = week?.UniqueVisitors ?? 0,
            VisitorsThisMonth = month?.UniqueVisitors ?? 0,
            AuthenticatedVisitorsThisMonth = month?.AuthenticatedUniqueVisitors ?? 0,
            SessionsThisMonth = month?.Sessions ?? 0,
            PageViewsThisMonth = month?.PageViews ?? 0
        };
    }

    private static DateTime? LatestOf(DateTime? first, DateTime? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;

    private async Task FanOutTransitionsAsync(
        List<(MonitoredApp App, AppHealthStatus Previous, AppHealthStatus Next)> transitions,
        CancellationToken ct)
    {
        var projectIds = transitions.Select(t => t.App.ProjectId).Distinct().ToList();
        var orgIds = await repo.GetProjectOrgIdsAsync(projectIds, ct).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var (app, previous, next) in transitions)
        {
            var orgId = orgIds.TryGetValue(app.ProjectId, out var oid) ? (int?)oid : null;
            await notifier.BroadcastAsync(ResourceType.Project, app.ProjectId, EntityChangeOps.Updated, ct, orgId)
                .ConfigureAwait(false);

            var eventType = next switch
            {
                AppHealthStatus.Down => "app.down",
                AppHealthStatus.Up when previous is AppHealthStatus.Down or AppHealthStatus.Degraded
                    => "app.recovered",
                _ => null
            };

            if (eventType is null)
                continue;

            logger.LogInformation("Monitored app {AppId} ({AppName}) transitioned {Previous} -> {Next}",
                app.Id, app.Name, previous, next);

            await notificationService.SendEventAsync(eventType, new
            {
                AppId = app.Id,
                AppName = app.Name,
                app.ProjectId,
                Status = next.ToString(),
                PreviousStatus = previous.ToString(),
                At = now
            }, ct).ConfigureAwait(false);
        }
    }

    private async Task BroadcastAppChangeAsync(int projectId, string op, CancellationToken ct)
    {
        var orgIds = await repo.GetProjectOrgIdsAsync([projectId], ct).ConfigureAwait(false);
        var orgId = orgIds.TryGetValue(projectId, out var oid) ? (int?)oid : null;
        await notifier.BroadcastAsync(ResourceType.Project, projectId, op, ct, orgId).ConfigureAwait(false);
    }

    private async Task<MonitoredAppDto> MapWithUptimeAsync(MonitoredApp app, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var windows = await repo.GetUptimeWindowsAsync([app.Id], now, ct).ConfigureAwait(false);
        return MapWithUptime(app, windows.GetValueOrDefault(app.Id), now);
    }

    private static MonitoredAppDto MapWithUptime(
        MonitoredApp app,
        AppUptimeWindowCounts uptime,
        DateTime now) =>
        new()
        {
            Id = app.Id,
            ProjectId = app.ProjectId,
            ProjectName = app.Project?.Name,
            EnvironmentId = app.EnvironmentId,
            EnvironmentName = app.Environment?.Name,
            ServerId = app.ServerId,
            ServerName = app.Server?.Name,
            Name = app.Name,
            ProbeUrl = app.ProbeUrl,
            ProbeIntervalSeconds = app.ProbeIntervalSeconds,
            ProbeTimeoutSeconds = app.ProbeTimeoutSeconds,
            ExpectedStatusCode = app.ExpectedStatusCode,
            FailureThreshold = app.FailureThreshold,
            RecoveryThreshold = app.RecoveryThreshold,
            Enabled = app.Enabled,
            CurrentStatus = app.CurrentStatus,
            LastStatusChangeAt = app.LastStatusChangeAt,
            LastCheckedAt = app.LastCheckedAt,
            LastResponseTimeMs = app.LastResponseTimeMs,
            CreatedAt = app.CreatedAt,
            Uptime24h = uptime.Total24h > 0 ? (double)uptime.Up24h / uptime.Total24h : null,
            Uptime7d = uptime.Total7d > 0 ? (double)uptime.Up7d / uptime.Total7d : null,
            Uptime90d = uptime.Total90d > 0 ? (double)uptime.Up90d / uptime.Total90d : null,
            HasIngestKey = app.IngestKeyHash is not null
                           && app.IngestKeyExpiresAt >= now,
            IngestKeyCreatedAt = app.IngestKeyCreatedAt,
            IngestDroppedCount = app.IngestDroppedCount,
            LastIngestAt = app.LastIngestAt
        };

    private static void ValidateProbeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new BadRequestException("Probe URL must be an absolute http(s) URL.");
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
