// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppMonitoring;

public interface IAppLogRepository
{
    Task AddLogsAsync(IEnumerable<AppLogEntry> logs, CancellationToken ct = default);
    Task<(List<AppLogEntry> Items, int TotalCount)> GetLogsAsync(
        int appId, DateTime since, int? minSeverity, string? search, int page, int pageSize, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null, string? sortBy = null, bool sortDescending = true);
    /// <summary>PLAN-013 lot 4: purges the logs of these applications only, each project having its own duration.</summary>
    Task<int> PurgeOlderThanAsync(DateTime cutoff, IReadOnlyCollection<int> appIds, CancellationToken ct = default);
}
