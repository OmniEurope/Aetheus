// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Git;

public class GitRepository(AppDbContext db) : IGitRepository
{
    public async Task<List<GitConnection>> GetConnectionsByProjectAsync(int projectId, CancellationToken ct = default)
    {
        return await db.GitConnections
            .AsNoTracking()
            .Where(g => g.ProjectId == projectId)
            .Include(g => g.ServiceConnection)
            .Include(g => g.Project)
            .OrderBy(g => g.RepositoryName)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<GitConnection?> GetConnectionDetailAsync(int id, CancellationToken ct = default)
    {
        return await db.GitConnections
            .AsNoTracking()
            .Where(g => g.Id == id)
            .Include(g => g.ServiceConnection)
            .Include(g => g.Project)
            .Include(g => g.BranchPolicies)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<GitConnection?> FindConnectionAsync(int id, CancellationToken ct = default)
    {
        return await db.GitConnections.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task AddConnectionAsync(GitConnection connection, CancellationToken ct = default)
    {
        await db.GitConnections.AddAsync(connection, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveConnectionAsync(GitConnection connection, CancellationToken ct = default)
    {
        db.GitConnections.Remove(connection);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<(List<PullRequest> Items, int TotalCount)> GetPullRequestsPagedAsync(
        int gitConnectionId, string? search, int page, int pageSize,
        PullRequestStatus? status = null, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = true,
        IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = db.PullRequests
            .AsNoTracking()
            .Where(p => p.GitConnectionId == gitConnectionId);
        // The grid's column header filters, before the count.
        query = GitPullRequestListQuery.Columns.ApplyFilters(query, columnFilters);

        if (status.HasValue)
            query = query.Where(p => p.Status == status.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            if (db.Database.IsRelational())
            {
                // ILike = case-insensitive LIKE (PostgreSQL). Matches the case-insensitive search the UI
                // expects; a trigram/expression index can back it later. Escape LIKE wildcards in the term.
                var pattern = $"%{search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
                query = query.Where(p => EF.Functions.ILike(p.Title, pattern) || EF.Functions.ILike(p.AuthorLogin, pattern));
            }
            else
            {
                // InMemory (unit tests) can't translate ILike; fall back to a plain Contains. NOTE: this
                // fallback is case-SENSITIVE and does not escape LIKE wildcards - it only exists so the
                // unit-test provider runs. Case-insensitive matching is a Postgres-only (prod) guarantee,
                // covered by integration tests, not by this branch.
                query = query.Where(p => p.Title.Contains(search) || p.AuthorLogin.Contains(search));
            }
        }

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);

        var items = await query
            .OrderByProperty(sortBy, sortDescending, p => p.ExternalCreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (items, totalCount);
    }

    public Task<List<string>> GetPullRequestAuthorsAsync(int gitConnectionId, CancellationToken ct = default) =>
        db.PullRequests.AsNoTracking()
            .Where(p => p.GitConnectionId == gitConnectionId && p.AuthorLogin != "")
            .Select(p => p.AuthorLogin)
            .Distinct()
            .OrderBy(author => author)
            .ToListAsync(ct);

    public async Task<PullRequest?> FindPullRequestByExternalIdAsync(int gitConnectionId, int externalId, CancellationToken ct = default)
    {
        return await db.PullRequests
            .Where(p => p.GitConnectionId == gitConnectionId && p.ExternalId == externalId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task AddPullRequestAsync(PullRequest pr, CancellationToken ct = default)
    {
        await db.PullRequests.AddAsync(pr, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<BranchPolicy>> GetBranchPoliciesAsync(int gitConnectionId, CancellationToken ct = default)
    {
        return await db.BranchPolicies
            .AsNoTracking()
            .Where(b => b.GitConnectionId == gitConnectionId)
            .OrderBy(b => b.BranchPattern)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<BranchPolicy?> FindBranchPolicyAsync(int id, CancellationToken ct = default)
    {
        return await db.BranchPolicies.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task AddBranchPolicyAsync(BranchPolicy policy, CancellationToken ct = default)
    {
        await db.BranchPolicies.AddAsync(policy, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveBranchPolicyAsync(BranchPolicy policy, CancellationToken ct = default)
    {
        db.BranchPolicies.Remove(policy);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Project behind a run, for authorizing a run-scoped git operation. An own-read: obtaining it by
    /// injecting IPipelineRepository loaded a whole run with its pipeline to keep one integer, and is
    /// what kept Git inside the cycle with Pipelines.
    /// </summary>
    public async Task<int?> GetRunProjectIdAsync(int pipelineRunId, CancellationToken ct = default) =>
        await db.PipelineRuns.AsNoTracking()
            .Where(run => run.Id == pipelineRunId)
            .Select(run => run.Pipeline.ProjectId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
}
