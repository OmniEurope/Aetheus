// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Rkhunter;

public class RkhunterRepository(AppDbContext db, TimeProvider timeProvider) : IRkhunterRepository
{
    public async Task<RkhunterState?> GetStateAsync(int serverId, CancellationToken ct = default)
    {
        return await db.RkhunterStates
            .AsNoTracking()
            .Where(r => r.ServerId == serverId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<RkhunterWarning>> GetWarningsAsync(int serverId, bool includeArchived = false, CancellationToken ct = default)
    {
        var query = db.RkhunterWarnings
            .AsNoTracking()
            .Where(w => w.ServerId == serverId);

        if (!includeArchived)
            query = query.Where(w => !w.IsArchived);

        return await query
            .OrderByDescending(w => w.FoundAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<RkhunterScanResult>> GetScanHistoryAsync(int serverId, int limit = 50, CancellationToken ct = default)
    {
        return await db.RkhunterScanResults
            .AsNoTracking()
            .Where(r => r.ServerId == serverId)
            .OrderByDescending(r => r.ScanTime)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<RkhunterScanResult> AddScanResultAsync(RkhunterScanResult result, CancellationToken ct = default)
    {
        db.RkhunterScanResults.Add(result);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return result;
    }

    public async Task AddTaskAsync(ServerTask task, CancellationToken ct = default)
    {
        await ServerTaskRepositoryOperations.AddTaskAsync(db, task, ct).ConfigureAwait(false);
    }

    public Task<bool> UpdateScanScheduleAsync(int serverId, string? cronExpression, CancellationToken ct = default) =>
        UpdateStateAsync(serverId, state => state.ScanScheduleCron = cronExpression, ct);

    public async Task<List<RkhunterState>> GetScheduledStatesAsync(CancellationToken ct = default)
    {
        return await db.RkhunterStates
            .AsNoTracking()
            .Where(r => r.ScanScheduleCron != null && r.ScanScheduleCron != "")
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task UpdateLastScheduledScanAsync(int serverId, CancellationToken ct = default)
    {
        await UpdateStateAsync(
            serverId,
            state => state.LastScheduledScanAt = timeProvider.GetUtcNow().UtcDateTime,
            ct).ConfigureAwait(false);
    }

    /// <summary>False when the server has no rkhunter state (rkhunter never installed): nothing is written.</summary>
    private async Task<bool> UpdateStateAsync(int serverId, Action<RkhunterState> update, CancellationToken ct)
    {
        var state = await db.RkhunterStates.FirstOrDefaultAsync(item => item.ServerId == serverId, ct)
            .ConfigureAwait(false);
        if (state is null) return false;
        update(state);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
