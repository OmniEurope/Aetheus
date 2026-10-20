// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Queries of <see cref="IPipelineEnvironmentReader"/>, moved unchanged from
/// <c>PipelineCoreRepository</c>; the <see cref="IPipelineRepository"/> facade delegates to them.</summary>
internal sealed class PipelineEnvironmentRepository(AppDbContext db) : IPipelineEnvironmentReader
{
    public async Task<Data.Entities.Environment?> FindEnvironmentByNameAsync(
        string name, int? projectId, CancellationToken ct = default)
    {
        // AsSplitQuery: two collection Includes on one query is a cartesian product (servers x checks).
        // AsNoTracking: every caller reads this environment to decide something, none of them mutates
        // it, and tracking an environment with its whole server list is the expensive half of the call.
        // BACK-018: a name is unique per project only, so the run's project's own environment comes
        // first, then a shared one (no project), never another project's; the order makes it deterministic.
        return await db.Environments
            .AsNoTracking()
            .Where(e => e.Name == name && (e.ProjectId == null || e.ProjectId == projectId))
            .Include(e => e.Servers).ThenInclude(es => es.Server)
            .Include(e => e.Checks)
            .AsSplitQuery()
            .OrderBy(e => e.ProjectId == null)
            .ThenBy(e => e.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<EnvironmentCheck>> GetEnvironmentChecksAsync(int environmentId, CancellationToken ct = default)
    {
        return await db.EnvironmentChecks
            .AsNoTracking()
            .Where(c => c.EnvironmentId == environmentId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct).ConfigureAwait(false);
    }
}
