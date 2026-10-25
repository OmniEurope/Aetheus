// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The run that holds a resource another run asked for.</summary>
public sealed record PipelineResourceLockHolder(string Key, int RunId, string PipelineName, int BuildNumber);

/// <summary>
/// Resources a pipeline run holds alone (decision of 2026-10-02). A lock counts only while its run is
/// active; a run started by one of the holder's trigger steps shares it, since it acts for that run.
/// </summary>
public interface IPipelineResourceLockRepository
{
    /// <summary>Takes every key for <paramref name="runId"/>, all or nothing.</summary>
    /// <returns>Null when the run now holds them all, otherwise the run that holds one of them.</returns>
    Task<PipelineResourceLockHolder?> TryAcquireAsync(int runId, IReadOnlyCollection<string> keys, CancellationToken ct = default);

    /// <summary>Releases everything <paramref name="runId"/> holds.</summary>
    Task<int> ReleaseAsync(int runId, CancellationToken ct = default);

    /// <summary>Whether <paramref name="runId"/>, or a run that started it, holds a resource.</summary>
    Task<bool> HoldsAnyAsync(int runId, CancellationToken ct = default);
}

internal sealed class PipelineResourceLockRepository(AppDbContext db, TimeProvider timeProvider) : IPipelineResourceLockRepository
{
    /// <summary>Deeper than any real chain (candidate -> qa), and a bound on a cycle in bad data.</summary>
    private const int MaxAncestorDepth = 6;

    private static readonly PipelineStatus[] ActiveStatuses =
        [PipelineStatus.Pending, PipelineStatus.Running, PipelineStatus.WaitingForApproval, PipelineStatus.AwaitingResume];

    public async Task<PipelineResourceLockHolder?> TryAcquireAsync(
        int runId, IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var wanted = keys.Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0) return null;

        var lineage = await GetRunAndAncestorsAsync(runId, ct).ConfigureAwait(false);
        var held = await db.PipelineResourceLocks.AsNoTracking()
            .Where(l => wanted.Contains(l.Key))
            .Select(l => new
            {
                l.Key,
                l.PipelineRunId,
                l.PipelineRun.Status,
                l.PipelineRun.BuildNumber,
                PipelineName = l.PipelineRun.Pipeline.Name
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var blocking = held.FirstOrDefault(l => !lineage.Contains(l.PipelineRunId) && ActiveStatuses.Contains(l.Status));
        if (blocking is not null)
            return new PipelineResourceLockHolder(blocking.Key, blocking.PipelineRunId, blocking.PipelineName, blocking.BuildNumber);

        // A finished holder no longer counts: its row goes, whatever ended it.
        foreach (var stale in held.Where(l => !lineage.Contains(l.PipelineRunId)))
            await DeleteAsync(l => l.Key == stale.Key && l.PipelineRunId == stale.PipelineRunId, ct).ConfigureAwait(false);

        var alreadyHeld = held.Where(l => lineage.Contains(l.PipelineRunId)).Select(l => l.Key).ToHashSet(StringComparer.Ordinal);
        var added = wanted.Where(key => !alreadyHeld.Contains(key))
            .Select(key => new PipelineResourceLock
            {
                Key = key,
                PipelineRunId = runId,
                AcquiredAt = timeProvider.GetUtcNow().UtcDateTime
            })
            .ToList();
        if (added.Count == 0) return null;

        db.PipelineResourceLocks.AddRange(added);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (DbUpdateException)
        {
            // Another run took one of them between the read and the write: the key is the mutex.
            Detach(added);
            var winner = await db.PipelineResourceLocks.AsNoTracking()
                .Where(l => wanted.Contains(l.Key) && l.PipelineRunId != runId)
                .Select(l => new PipelineResourceLockHolder(l.Key, l.PipelineRunId, l.PipelineRun.Pipeline.Name, l.PipelineRun.BuildNumber))
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            // Nothing found means the other writer let go already: the next pass takes it.
            return winner ?? new PipelineResourceLockHolder(wanted[0], 0, string.Empty, 0);
        }
        finally
        {
            // Lock rows are written and deleted here only, never read back through tracking: a row
            // left tracked would collide with the next acquisition of its key in the same scope.
            Detach(added);
        }
    }

    private void Detach(IEnumerable<PipelineResourceLock> locks)
    {
        foreach (var entry in locks) db.Entry(entry).State = EntityState.Detached;
    }

    public Task<int> ReleaseAsync(int runId, CancellationToken ct = default) =>
        DeleteAsync(l => l.PipelineRunId == runId, ct);

    private async Task<int> DeleteAsync(
        System.Linq.Expressions.Expression<Func<PipelineResourceLock, bool>> which, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            return await db.PipelineResourceLocks.Where(which).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        else
        {
            // In-memory provider (unit tests): no bulk delete, the rows go through the tracker.
            var rows = await db.PipelineResourceLocks.Where(which).ToListAsync(ct).ConfigureAwait(false);
            db.PipelineResourceLocks.RemoveRange(rows);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            Detach(rows);
            return rows.Count;
        }
    }

    public async Task<bool> HoldsAnyAsync(int runId, CancellationToken ct = default)
    {
        var lineage = await GetRunAndAncestorsAsync(runId, ct).ConfigureAwait(false);
        return await db.PipelineResourceLocks.AsNoTracking()
            .AnyAsync(l => lineage.Contains(l.PipelineRunId), ct).ConfigureAwait(false);
    }

    /// <summary>The run itself and the runs whose trigger steps started it, closest first.</summary>
    private async Task<HashSet<int>> GetRunAndAncestorsAsync(int runId, CancellationToken ct)
    {
        var lineage = new HashSet<int> { runId };
        var current = runId;
        for (var depth = 0; depth < MaxAncestorDepth; depth++)
        {
            var parent = await db.PipelineStepRuns.AsNoTracking()
                .Where(step => step.TriggeredRunId == current)
                .OrderBy(step => step.Id)
                .Select(step => (int?)step.PipelineRunId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (parent is null || !lineage.Add(parent.Value)) break;
            current = parent.Value;
        }
        return lineage;
    }
}
