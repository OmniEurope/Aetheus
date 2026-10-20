// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Git;

public class GitLightRepository(AppDbContext db) : IGitLightRepository
{
    public async Task<List<GitInternalRepo>> GetByProjectAsync(int projectId, CancellationToken ct = default)
    {
        return await db.GitInternalRepos
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .Include(r => r.Project)
            .Include(r => r.GitConnection)
            .OrderBy(r => r.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<GitInternalRepo>> GetAccessibleAsync(List<int>? projectIds, CancellationToken ct = default)
    {
        IQueryable<GitInternalRepo> query = db.GitInternalRepos.AsNoTracking();
        if (projectIds is not null)
            query = query.Where(r => projectIds.Contains(r.ProjectId));
        return await query
            .Include(r => r.Project)
            .Include(r => r.GitConnection)
            .OrderBy(r => r.Project!.Name)
            .ThenBy(r => r.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<(List<GitInternalRepo> Items, int TotalCount)> GetAccessiblePagedAsync(
        List<int>? projectIds, int? projectId, string? search, string? sortBy,
        bool sortDescending, int page, int pageSize, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? columnFilters = null)
    {
        // The grid's column header filters, after the scope and before the count.
        var query = GitRepositoryListQuery.Columns.ApplyFilters(FilterAccessible(projectIds, projectId, search), columnFilters);
        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await OrderAccessible(query, sortBy, sortDescending)
            .Include(r => r.Project)
            .Include(r => r.GitConnection)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (items, totalCount);
    }

    public Task<List<string>> GetDefaultBranchesAsync(List<int>? projectIds, int? projectId, CancellationToken ct = default) =>
        FilterAccessible(projectIds, projectId, search: null)
            .Select(r => r.DefaultBranch)
            .Distinct()
            .OrderBy(branch => branch)
            .ToListAsync(ct);

    private IQueryable<GitInternalRepo> FilterAccessible(
        List<int>? projectIds, int? projectId, string? search)
    {
        IQueryable<GitInternalRepo> query = db.GitInternalRepos.AsNoTracking();
        if (projectIds is not null)
            query = query.Where(r => projectIds.Contains(r.ProjectId));
        if (projectId is not null)
            query = query.Where(r => r.ProjectId == projectId.Value);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(r => r.Name.Contains(search)
                || r.Slug.Contains(search)
                || (r.Description != null && r.Description.Contains(search))
                || r.Project.Name.Contains(search));
        return query;
    }

    internal static IOrderedQueryable<GitInternalRepo> OrderAccessible(
        IQueryable<GitInternalRepo> query, string? sortBy, bool sortDescending) =>
        (sortBy, sortDescending) switch
        {
            ("ProjectName", false) => query.OrderBy(r => r.Project.Name).ThenBy(r => r.Name),
            ("ProjectName", true) => query.OrderByDescending(r => r.Project.Name).ThenBy(r => r.Name),
            ("Description", false) => query.OrderBy(r => r.Description),
            ("Description", true) => query.OrderByDescending(r => r.Description),
            ("DefaultBranch", false) => query.OrderBy(r => r.DefaultBranch),
            ("DefaultBranch", true) => query.OrderByDescending(r => r.DefaultBranch),
            ("CreatedAt", false) => query.OrderBy(r => r.CreatedAt),
            ("CreatedAt", true) => query.OrderByDescending(r => r.CreatedAt),
            ("LastPushAt", false) => query.OrderBy(r => r.LastPushAt),
            ("LastPushAt", true) => query.OrderByDescending(r => r.LastPushAt),
            ("Name", true) => query.OrderByDescending(r => r.Name),
            _ => query.OrderBy(r => r.Name)
        };

    public async Task<GitInternalRepo?> FindByIdAsync(int id, CancellationToken ct = default)
    {
        return await db.GitInternalRepos.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task<GitInternalRepo?> FindByIdWithProjectAsync(int id, CancellationToken ct = default)
    {
        return await db.GitInternalRepos
            .Where(r => r.Id == id)
            .Include(r => r.Project)
            .Include(r => r.GitConnection)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<GitInternalRepo?> FindBySlugAsync(int projectId, string slug, CancellationToken ct = default)
    {
        return await db.GitInternalRepos
            .Where(r => r.ProjectId == projectId && r.Slug == slug)
            .Include(r => r.Project)
            .Include(r => r.GitConnection)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<ProjectRepositorySource?> GetProjectRepositorySourceAsync(int projectId, CancellationToken ct = default)
    {
        return await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new ProjectRepositorySource(p.RepositoryUrl, p.GitConnectionId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<string?> GetProjectDefaultBranchAsync(int projectId, CancellationToken ct = default)
    {
        return await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.DefaultBranch)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> ProjectExistsAsync(int projectId, CancellationToken ct = default)
    {
        return await db.Projects
            .AsNoTracking()
            .AnyAsync(p => p.Id == projectId, ct)
            .ConfigureAwait(false);
    }

    public async Task<List<GitInternalRepo>> GetAllAsync(CancellationToken ct = default)
    {
        return await db.GitInternalRepos
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(GitInternalRepo entity, CancellationToken ct = default)
    {
        await db.GitInternalRepos.AddAsync(entity, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(GitInternalRepo entity, CancellationToken ct = default)
    {
        db.GitInternalRepos.Remove(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> GetNextPrNumberAsync(int gitConnectionId, CancellationToken ct = default)
    {
        var maxNumber = await db.PullRequests
            .Where(p => p.GitConnectionId == gitConnectionId)
            .MaxAsync(p => (int?)p.ExternalId, ct)
            .ConfigureAwait(false);
        return (maxNumber ?? 0) + 1;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<int?> MarkCommittedAsync(int id, DateTime committedAt, CancellationToken ct = default)
    {
        var projectId = await db.GitInternalRepos.AsNoTracking()
            .Where(repository => repository.Id == id)
            .Select(repository => (int?)repository.ProjectId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (projectId is null) return null;

        if (db.Database.IsRelational())
        {
            // A single UPDATE of this row: the caller's own pending changes (a pipeline being saved or
            // rolled back) are neither flushed early nor re-tried by this write.
            await db.GitInternalRepos
                .Where(repository => repository.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(repository => repository.IsEmpty, false)
                    .SetProperty(repository => repository.LastPushAt, (DateTime?)committedAt)
                    .SetProperty(repository => repository.UpdatedAt, committedAt), ct)
                .ConfigureAwait(false);
            return projectId;
        }

        var stored = await db.GitInternalRepos.FirstAsync(repository => repository.Id == id, ct).ConfigureAwait(false);
        stored.IsEmpty = false;
        stored.LastPushAt = committedAt;
        stored.UpdatedAt = committedAt;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return projectId;
    }

    // ── Branch Protection ────────────────────────────────────────────

    public async Task<List<BranchProtectionRule>> GetBranchProtectionRulesAsync(int repoId, CancellationToken ct = default)
    {
        return await db.BranchProtectionRules
            .AsNoTracking()
            .Where(r => r.GitInternalRepoId == repoId)
            .OrderBy(r => r.Pattern)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<BranchProtectionRule?> FindBranchProtectionRuleAsync(int id, CancellationToken ct = default)
    {
        return await db.BranchProtectionRules.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task AddBranchProtectionRuleAsync(BranchProtectionRule rule, CancellationToken ct = default)
    {
        await db.BranchProtectionRules.AddAsync(rule, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveBranchProtectionRuleAsync(BranchProtectionRule rule, CancellationToken ct = default)
    {
        db.BranchProtectionRules.Remove(rule);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // --- Own-reads over PipelineRun ---------------------------------------------------------------
    // Two single-column reads this module obtained by injecting IPipelineRepository, which is what
    // kept Git inside the cycle with Pipelines. Both loaded a full run with its pipeline to keep one
    // value; neither writes anything.

    /// <summary>Whether a run is still executing - the gate on serving a run-scoped clone token.</summary>
    public async Task<bool> IsRunActiveAsync(int runId, CancellationToken ct = default) =>
        await db.PipelineRuns.AsNoTracking()
            .Where(run => run.Id == runId)
            .Select(run => run.Status)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) == PipelineStatus.Running;

    /// <summary>Project behind a run, for authorizing a run-scoped git operation.</summary>
    public async Task<int?> GetRunProjectIdAsync(int pipelineRunId, CancellationToken ct = default) =>
        await db.PipelineRuns.AsNoTracking()
            .Where(run => run.Id == pipelineRunId)
            .Select(run => run.Pipeline.ProjectId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
}
