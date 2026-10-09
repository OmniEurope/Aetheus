// SPDX-License-Identifier: EUPL-1.2
using System.Data;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Recette R2-110: the per-project release counter behind <c>BUILD_RELEASENUMBER</c>, the last number
/// of the application version. Same reservation contract as <see cref="PipelineBuildNumberRepository"/>:
/// the increment and its read-back share one transaction whose row lock on the project makes two
/// concurrent releases of one project take two numbers, never the same one.
/// </summary>
internal sealed class ProjectReleaseNumberRepository(AppDbContext db)
{
    /// <summary>Takes the project's next release number (0 for its first release), or null when the project no longer exists.</summary>
    public async Task<int?> AllocateNextAsync(int projectId, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            // InMemory (unit tests): no bulk update and no row locks, so increment the tracked entity.
            var tracked = await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, ct).ConfigureAwait(false);
            if (tracked is null) return null;
            tracked.ReleaseCounter++;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return tracked.ReleaseCounter - 1;
        }

        // Join the caller's transaction when there is one (see PipelineBuildNumberRepository).
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false)
            : null;
        var rows = await db.Projects
            .Where(project => project.Id == projectId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(project => project.ReleaseCounter, project => project.ReleaseCounter + 1),
                ct)
            .ConfigureAwait(false);
        int? allocated = null;
        if (rows > 0)
        {
            allocated = await db.Projects.AsNoTracking()
                .Where(project => project.Id == projectId)
                .Select(project => project.ReleaseCounter - 1)
                .SingleAsync(ct).ConfigureAwait(false);
        }
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);

        // ExecuteUpdateAsync bypasses the change tracker: a tracked Project must not write back a stale counter.
        var trackedProject = db.ChangeTracker.Entries<Project>().FirstOrDefault(entry => entry.Entity.Id == projectId);
        if (trackedProject is not null)
            trackedProject.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        return allocated;
    }

    /// <summary>The last release number the project handed out (0 before its first release), or null when it does not exist.</summary>
    public async Task<int?> GetLatestAsync(int projectId, CancellationToken ct)
    {
        var counter = await db.Projects.AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => (int?)project.ReleaseCounter)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return counter is { } value ? Math.Max(value - 1, 0) : null;
    }

    /// <summary>
    /// The release number of a run, or null when it has none. Read for the run that started another
    /// (UPSTREAM_RUN_ID), which only the engine writes: a launch parameter of that name is refused.
    /// </summary>
    public Task<int?> GetRunReleaseNumberAsync(int runId, CancellationToken ct) =>
        db.PipelineRuns.AsNoTracking()
            .Where(run => run.Id == runId)
            .Select(run => run.ReleaseNumber)
            .FirstOrDefaultAsync(ct);
}
