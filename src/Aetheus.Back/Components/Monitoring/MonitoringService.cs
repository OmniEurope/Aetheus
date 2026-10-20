// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Monitoring;

public class MonitoringService(IMonitoringRepository repo, TimeProvider timeProvider) : IMonitoringService
{
    /// <summary>The run groups the dashboard tile shows (<c>MaxGroups</c> on the Home
    /// page).</summary>
    private const int RecentRunGroups = 10;

    public Task<DashboardOverviewDto> GetDashboardAsync(
        List<int>? accessibleServerIds = null,
        List<int>? accessibleProjectIds = null,
        CancellationToken ct = default) =>
        GetDashboardCoreAsync(accessibleServerIds, accessibleProjectIds, [], includeDirectPipelineAccess: false, ct);

    public Task<DashboardOverviewDto> GetDashboardAsync(
        List<int>? accessibleServerIds,
        List<int>? accessibleProjectIds,
        List<int>? accessiblePipelineIds,
        CancellationToken ct) =>
        GetDashboardCoreAsync(accessibleServerIds, accessibleProjectIds, accessiblePipelineIds, includeDirectPipelineAccess: true, ct);

    private async Task<DashboardOverviewDto> GetDashboardCoreAsync(
        List<int>? accessibleServerIds,
        List<int>? accessibleProjectIds,
        List<int>? accessiblePipelineIds,
        bool includeDirectPipelineAccess,
        CancellationToken ct)
    {
        // DbContext is not thread-safe - execute sequentially.
        var servers = await repo.GetDashboardServersAsync(accessibleServerIds, ct).ConfigureAwait(false);
        var pendingTasks = await repo.CountPendingTasksAsync(accessibleServerIds, ct).ConfigureAwait(false);
        var runningPipelines = includeDirectPipelineAccess
            ? await repo.CountRunningPipelinesAsync(accessibleProjectIds, accessiblePipelineIds, ct).ConfigureAwait(false)
            : await repo.CountRunningPipelinesAsync(accessibleProjectIds, ct).ConfigureAwait(false);
        var recentRuns = includeDirectPipelineAccess
            ? await repo.GetRecentRunsAsync(RecentRunGroups, accessibleProjectIds, accessiblePipelineIds, ct).ConfigureAwait(false)
            : await repo.GetRecentRunsAsync(RecentRunGroups, accessibleProjectIds, ct).ConfigureAwait(false);
        var recentProjects = await repo.GetRecentProjectsAsync(10, accessibleProjectIds, ct).ConfigureAwait(false);
        var totalProjects = await repo.CountProjectsAsync(accessibleProjectIds, ct).ConfigureAwait(false);

        return new DashboardOverviewDto
        {
            TotalServers = servers.Count,
            OnlineServers = servers.Count(s => s.Status == ServerStatus.Online),
            OfflineServers = servers.Count(s => s.Status == ServerStatus.Offline),
            PendingTasks = pendingTasks,
            RunningPipelines = runningPipelines,
            TotalProjects = totalProjects,
            // Use shared run projection, warning hydration and grade aggregation for consistent
            // fields across run grids.
            RecentRuns = recentRuns,
            // Projected and ordered by the database (online first, then most recently active). The
            // tile lists every readable server, so the counts come from those same rows.
            Servers = servers,
            Projects = recentProjects.Select(project =>
                ProjectDtoMapper.ToDto(project, includePipelineSummary: true)).ToList()
        };
    }

    public async Task<List<ServerMetricDto>> GetServerMetricsAsync(
        int serverId,
        int hours,
        CancellationToken ct = default,
        DateTime? afterUtc = null,
        int take = 1_000)
    {
        var since = timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours);
        var metrics = await repo.GetServerMetricsSinceAsync(
            serverId,
            since,
            ct,
            afterUtc,
            take).ConfigureAwait(false);

        return metrics.Select(ServerMetricMapper.ToDto).ToList();
    }
}
