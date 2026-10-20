// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Releases;

/// <summary>
/// Recette R1-72, decision D2 (PLAN-002 lot 3). A release is dated by its publication, else by its
/// detection, else, for the rows created before the detection date was stamped (they hold the
/// 0001-01-01 default), by the start of the run that published it or last recorded it. A release with
/// none of these dates is kept: its age cannot be told. A dated release older than the window is
/// archived unless it is protected by the same rules as its payloads in
/// <c>ArtifactRepository.GetExpiredAsync</c>, plus what archiving it would break:
/// <list type="bullet">
/// <item>the deployed release, and the last three deployed releases of the project (production, N-1 and
/// N-2: the rollback window);</item>
/// <item>a release one of whose artifacts holds an active retention lease or a pending Dependency-Track delivery;</item>
/// <item>a release whose run has not ended;</item>
/// <item>a release named by a rollback (its history);</item>
/// <item>the newest release of each project, which the <c>latest</c> selector of a deployment resolves.</item>
/// </list>
/// Archiving keeps the release row, its version, commit, branch and links to commits and branches, so a
/// complete pipeline can run again from its commit. It deletes its data: the artifacts no live release
/// still links (their files then have no row and <c>ArtifactCleanupService.PurgeUnreferencedFilesAsync</c>
/// removes them), and the runs that published or recorded it when nothing live needs them.
/// </summary>
public sealed class ReleaseRetentionRepository(AppDbContext db) : IReleaseRetentionRepository
{
    /// <summary>Same window as <c>ArtifactRepository.RetainedDeployedReleases</c>.</summary>
    internal const int RetainedDeployedReleases = 3;

    /// <summary>A detection date before this is the unset default, not a date.</summary>
    internal static readonly DateTime DatedFloor = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public Task<List<int>> GetExpiredReleaseIdsAsync(DateTime cutoff, DateTime now, int batchSize, CancellationToken ct = default) =>
        Expired(cutoff, now)
            .OrderBy(release => release.Id)
            .Select(release => release.Id)
            .Take(batchSize)
            .ToListAsync(ct);

    public async Task<int> ArchiveExpiredAsync(
        IReadOnlyCollection<int> releaseIds, DateTime cutoff, DateTime now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(releaseIds);
        if (releaseIds.Count == 0) return 0;
        if (!db.Database.IsRelational())
            return await ArchiveTrackedAsync(releaseIds, cutoff, now, ct).ConfigureAwait(false);

        // One transaction: a release is never left archived with its data, nor its data gone with the
        // release still live. The selection is only a candidate list: the update repeats the whole
        // rule, so a release deployed, leased or rolled back to since then is not archived.
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var archived = await Expired(cutoff, now)
            .Where(release => releaseIds.Contains(release.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(release => release.Status, ReleaseStatus.Archived)
                .SetProperty(release => release.ArchivedAt, (DateTime?)now), ct)
            .ConfigureAwait(false);
        if (archived > 0)
        {
            var artifacts = PurgeableArtifacts(releaseIds, now);
            // A completed Dependency-Track delivery no longer needs its payload, but its foreign key
            // refuses the delete (ON DELETE RESTRICT): the history row stays, without the link.
            await db.DependencyTrackOutboxItems
                .Where(item => item.CompletedAt != null && artifacts.Any(artifact => artifact.Id == item.PipelineArtifactId))
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.PipelineArtifactId, (int?)null), ct)
                .ConfigureAwait(false);
            await artifacts.ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await PurgeableRuns(releaseIds, now).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return archived;
    }

    /// <summary>The same archive for the in-memory provider of the unit tests, which has no bulk
    /// statements; its cascades apply to the tracked rows.</summary>
    private async Task<int> ArchiveTrackedAsync(
        IReadOnlyCollection<int> releaseIds, DateTime cutoff, DateTime now, CancellationToken ct)
    {
        var releases = await Expired(cutoff, now).Where(release => releaseIds.Contains(release.Id))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var release in releases)
        {
            release.Status = ReleaseStatus.Archived;
            release.ArchivedAt = now;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (releases.Count == 0) return 0;

        var artifacts = await PurgeableArtifacts(releaseIds, now).ToListAsync(ct).ConfigureAwait(false);
        var artifactIds = artifacts.Select(artifact => artifact.Id).ToList();
        var deliveries = await db.DependencyTrackOutboxItems
            .Where(item => item.CompletedAt != null && item.PipelineArtifactId != null
                && artifactIds.Contains(item.PipelineArtifactId.Value))
            .ToListAsync(ct).ConfigureAwait(false);
        deliveries.ForEach(item => item.PipelineArtifactId = null);
        db.PipelineArtifacts.RemoveRange(artifacts);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var runs = await PurgeableRuns(releaseIds, now).Include(run => run.Artifacts)
            .ToListAsync(ct).ConfigureAwait(false);
        var runIds = runs.Select(run => run.Id).ToList();
        foreach (var release in await db.Releases
                     .Where(release => (release.PipelineRunId != null && runIds.Contains(release.PipelineRunId.Value))
                         || (release.CreatedByPipelineRunId != null && runIds.Contains(release.CreatedByPipelineRunId.Value)))
                     .ToListAsync(ct).ConfigureAwait(false))
        {
            if (release.PipelineRunId is { } recorded && runIds.Contains(recorded)) release.PipelineRunId = null;
            if (release.CreatedByPipelineRunId is { } created && runIds.Contains(created)) release.CreatedByPipelineRunId = null;
        }
        db.PipelineRuns.RemoveRange(runs);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return releases.Count;
    }

    /// <summary>The artifacts of the archived releases among <paramref name="releaseIds"/> that no live
    /// release links and that no lease or pending delivery still holds.</summary>
    private IQueryable<PipelineArtifact> PurgeableArtifacts(IReadOnlyCollection<int> releaseIds, DateTime now) =>
        db.PipelineArtifacts.Where(artifact =>
            artifact.Releases.Any(release => releaseIds.Contains(release.Id) && release.Status == ReleaseStatus.Archived)
            && artifact.Releases.All(release => release.Status == ReleaseStatus.Archived)
            && !(artifact.RetentionLeaseExpiresAt != null && artifact.RetentionLeaseExpiresAt > now)
            && !db.DependencyTrackOutboxItems.Any(item =>
                item.PipelineArtifactId == artifact.Id && item.CompletedAt == null));

    /// <summary>
    /// The runs that published or last recorded the archived releases among <paramref name="releaseIds"/>,
    /// once nothing live needs them: ended, named by no live release, holding no artifact a live release
    /// links, nor a leased artifact, a pending delivery or a deferred cleanup still to run. Deleting a run
    /// takes its tasks, logs and remaining artifacts with it.
    /// </summary>
    private IQueryable<PipelineRun> PurgeableRuns(IReadOnlyCollection<int> releaseIds, DateTime now) =>
        db.PipelineRuns.Where(run =>
            db.Releases.Any(release => releaseIds.Contains(release.Id) && release.Status == ReleaseStatus.Archived
                && (release.PipelineRunId == run.Id || release.CreatedByPipelineRunId == run.Id))
            && PipelineStatusFacts.Terminal.Contains(run.Status)
            && !db.Releases.Any(release => release.Status != ReleaseStatus.Archived
                && (release.PipelineRunId == run.Id || release.CreatedByPipelineRunId == run.Id)))
        .Where(run =>
            !run.Artifacts.Any(artifact =>
                artifact.Releases.Any(release => release.Status != ReleaseStatus.Archived)
                || (artifact.RetentionLeaseExpiresAt != null && artifact.RetentionLeaseExpiresAt > now)
                || db.DependencyTrackOutboxItems.Any(item => item.PipelineArtifactId == artifact.Id))
            && !run.Tasks.Any(task => task.IsDeferredCleanup
                && (task.Status == TaskExecutionStatus.Pending
                    || task.Status == TaskExecutionStatus.Assigned
                    || task.Status == TaskExecutionStatus.Running)));

    private IQueryable<Release> Expired(DateTime cutoff, DateTime now) =>
        // Chained Where calls are the same conjunction for EF (one SQL WHERE); split only so that each
        // predicate stays within the complexity grade.
        db.Releases.Where(release =>
            release.Status != ReleaseStatus.Archived
            && (release.PublishedAt
                ?? (release.DetectedAt > DatedFloor
                    ? (DateTime?)release.DetectedAt
                    : release.CreatedByPipelineRun != null
                        ? (DateTime?)release.CreatedByPipelineRun.StartedAt
                        : release.PipelineRun != null
                            ? (DateTime?)release.PipelineRun.StartedAt
                            : (DateTime?)null)) <= cutoff)
        .Where(release =>
            release.Status != ReleaseStatus.Deployed
            && !(release.Status == ReleaseStatus.Superseded
                && db.Releases.Count(newer => newer.ProjectId == release.ProjectId
                    && (newer.Status == ReleaseStatus.Deployed || newer.Status == ReleaseStatus.Superseded)
                    && (newer.PublishedAt ?? newer.DetectedAt) > (release.PublishedAt ?? release.DetectedAt))
                    < RetainedDeployedReleases)
            && !release.Artifacts.Any(artifact =>
                (artifact.RetentionLeaseExpiresAt != null && artifact.RetentionLeaseExpiresAt > now)
                || db.DependencyTrackOutboxItems.Any(item =>
                    item.PipelineArtifactId == artifact.Id && item.CompletedAt == null)))
        .Where(release =>
            !(release.PipelineRun != null && !PipelineStatusFacts.Terminal.Contains(release.PipelineRun.Status))
            && !(release.CreatedByPipelineRun != null
                && !PipelineStatusFacts.Terminal.Contains(release.CreatedByPipelineRun.Status))
            && !release.RollbacksFrom.Any()
            && !release.RollbacksTo.Any()
            && db.Releases.Any(newer => newer.ProjectId == release.ProjectId && newer.Id > release.Id));
}
