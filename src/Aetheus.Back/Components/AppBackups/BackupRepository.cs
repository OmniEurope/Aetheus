// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppBackups;

internal sealed class BackupRepository(AppDbContext db) : IBackupRepository
{
    public void AddPolicy(BackupPolicy policy) => db.BackupPolicies.Add(policy);
    public void DeletePolicy(BackupPolicy policy) => db.BackupPolicies.Remove(policy);

    public async Task<(List<BackupPolicy> Items, int TotalCount)> GetPoliciesPagedAsync(
        IReadOnlyCollection<int>? projectIds, string? search, string? sortBy,
        bool sortDescending, int page, int pageSize, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = db.BackupPolicies.AsNoTracking().AsQueryable();
        if (projectIds is not null)
            query = query.Where(p => projectIds.Contains(p.ProjectId));
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => p.Name.Contains(search)
                || (p.Project != null && p.Project.Name.Contains(search))
                || (p.Server != null && p.Server.Name.Contains(search)));
        // The grid's column header filters, after the scope and before the count.
        query = BackupListQuery.PolicyColumns.ApplyFilters(query, columnFilters);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        var ordered = OrderPolicies(query, sortBy, sortDescending);
        var items = await ordered
            .Include(p => p.Project)
            .Include(p => p.Server)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (items, totalCount);
    }

    private static IQueryable<BackupPolicy> OrderPolicies(
        IQueryable<BackupPolicy> query,
        string? sortBy,
        bool sortDescending) => sortDescending
            ? OrderPoliciesDescending(query, sortBy)
            : OrderPoliciesAscending(query, sortBy);

    private static IQueryable<BackupPolicy> OrderPoliciesAscending(
        IQueryable<BackupPolicy> query,
        string? sortBy) =>
        sortBy switch
        {
            "ProjectName" => query.OrderBy(p => p.Project!.Name),
            "ServerName" => query.OrderBy(p => p.Server!.Name),
            "DbEngine" => query.OrderBy(p => p.DbEngine),
            "ScheduleCron" => query.OrderBy(p => p.ScheduleCron),
            "RetentionCount" => query.OrderBy(p => p.RetentionCount),
            "LastRunAt" => query.OrderBy(p => p.LastRunAt),
            "Enabled" => query.OrderBy(p => p.Enabled),
            _ => query.OrderBy(p => p.Name)
        };

    private static IQueryable<BackupPolicy> OrderPoliciesDescending(
        IQueryable<BackupPolicy> query,
        string? sortBy) =>
        sortBy switch
        {
            "ProjectName" => query.OrderByDescending(p => p.Project!.Name),
            "ServerName" => query.OrderByDescending(p => p.Server!.Name),
            "DbEngine" => query.OrderByDescending(p => p.DbEngine),
            "ScheduleCron" => query.OrderByDescending(p => p.ScheduleCron),
            "RetentionCount" => query.OrderByDescending(p => p.RetentionCount),
            "LastRunAt" => query.OrderByDescending(p => p.LastRunAt),
            "Enabled" => query.OrderByDescending(p => p.Enabled),
            _ => query.OrderByDescending(p => p.Name)
        };

    public async Task<BackupPolicy?> FindPolicyAsync(int id, CancellationToken ct = default)
        => await db.BackupPolicies.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);

    public async Task<int?> GetProjectOrganizationIdAsync(int projectId, CancellationToken ct = default)
        => await db.Projects.AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => (int?)project.OrganizationId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task<List<BackupPolicy>> GetEnabledPoliciesAsync(CancellationToken ct = default)
        => await db.BackupPolicies
            .Where(p => p.Enabled)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public void AddRun(BackupRun run) => db.BackupRuns.Add(run);

    public async Task<BackupRun?> FindRunAsync(int id, CancellationToken ct = default)
        => await db.BackupRuns.FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);

    public async Task<BackupRun?> FindRunWithPolicyAsync(int id, CancellationToken ct = default)
        => await db.BackupRuns.Include(r => r.BackupPolicy)
            .FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);

    public async Task<(List<BackupRun> Items, int TotalCount)> GetRunsForPolicyPagedAsync(
        int policyId, string? search, string? sortBy, bool sortDescending,
        int page, int pageSize, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = db.BackupRuns.AsNoTracking().Where(r => r.BackupPolicyId == policyId);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(r => r.Message != null && r.Message.Contains(search));
        // The run grid's column header filters, before the count.
        query = BackupListQuery.RunColumns.ApplyFilters(query, columnFilters);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        var ordered = (sortBy, sortDescending) switch
        {
            ("Status", false) => query.OrderBy(r => r.Status),
            ("Status", true) => query.OrderByDescending(r => r.Status),
            ("RestoreCheckStatus", false) => query.OrderBy(r => r.RestoreCheckStatus),
            ("RestoreCheckStatus", true) => query.OrderByDescending(r => r.RestoreCheckStatus),
            ("SizeBytes", false) => query.OrderBy(r => r.SizeBytes),
            ("SizeBytes", true) => query.OrderByDescending(r => r.SizeBytes),
            ("StartedAt", false) => query.OrderBy(r => r.StartedAt),
            _ => query.OrderByDescending(r => r.StartedAt)
        };
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return (items, totalCount);
    }

    public async Task<BackupRun?> GetLatestSuccessfulRunAsync(int policyId, CancellationToken ct = default)
        => await db.BackupRuns
            .Where(r => r.BackupPolicyId == policyId && r.Status == BackupRunStatus.Succeeded)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await db.SaveChangesAsync(ct).ConfigureAwait(false);
}
