// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Monitoring;

public interface IMonitoringRepository
{
    /// <summary>The servers the dashboard tile lists, projected to the columns it shows and ordered
    /// online first, then most recently active.</summary>
    Task<List<ServerDto>> GetDashboardServersAsync(List<int>? accessibleIds = null, CancellationToken ct = default);

    Task<int> CountPendingTasksAsync(List<int>? accessibleServerIds = null, CancellationToken ct = default);

    Task<int> CountRunningPipelinesAsync(List<int>? accessibleProjectIds = null, CancellationToken ct = default);
    Task<int> CountRunningPipelinesAsync(List<int>? accessibleProjectIds, List<int>? accessiblePipelineIds, CancellationToken ct);

    /// <summary>Use the shared pipeline run projection and hydration for consistent dashboard
    /// fields. groupCount counts root-and-descendant groups rather than rows.</summary>
    Task<List<PipelineRunDto>> GetRecentRunsAsync(int groupCount, List<int>? accessibleProjectIds = null, CancellationToken ct = default);
    Task<List<PipelineRunDto>> GetRecentRunsAsync(int groupCount, List<int>? accessibleProjectIds, List<int>? accessiblePipelineIds, CancellationToken ct);

    Task<List<Project>> GetRecentProjectsAsync(int count, List<int>? accessibleProjectIds = null, CancellationToken ct = default);

    /// <summary>Every project the caller can read, not only the recent ones listed on the dashboard.</summary>
    Task<int> CountProjectsAsync(List<int>? accessibleProjectIds = null, CancellationToken ct = default);

    Task<List<ServerMetric>> GetServerMetricsSinceAsync(
        int serverId,
        DateTime since,
        CancellationToken ct = default,
        DateTime? afterUtc = null,
        int take = 1_000);
}
