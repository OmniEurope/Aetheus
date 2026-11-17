// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Rkhunter;

public interface IRkhunterRepository
{
    Task<RkhunterState?> GetStateAsync(int serverId, CancellationToken ct = default);
    Task<List<RkhunterWarning>> GetWarningsAsync(int serverId, bool includeArchived = false, CancellationToken ct = default);
    Task<List<RkhunterScanResult>> GetScanHistoryAsync(int serverId, int limit = 50, CancellationToken ct = default);
    Task<RkhunterScanResult> AddScanResultAsync(RkhunterScanResult result, CancellationToken ct = default);
    Task AddTaskAsync(ServerTask task, CancellationToken ct = default);
    /// <summary>False when the server has no rkhunter state (not installed): nothing was stored.</summary>
    Task<bool> UpdateScanScheduleAsync(int serverId, string? cronExpression, CancellationToken ct = default);
    Task<List<RkhunterState>> GetScheduledStatesAsync(CancellationToken ct = default);
    Task UpdateLastScheduledScanAsync(int serverId, CancellationToken ct = default);
}
