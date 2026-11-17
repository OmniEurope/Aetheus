// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Pipeline artifact persistence isolated from the broader pipeline data-access collaborator, and the
/// implementation of <see cref="IPipelineArtifactRepository"/> (the owning-pipeline query moved unchanged
/// from <c>PipelineCoreRepository</c>).
/// </summary>
internal sealed class PipelineArtifactRepository(AppDbContext db) : IPipelineArtifactRepository
{
    public async Task<List<PipelineArtifact>> GetArtifactsAsync(int runId, CancellationToken ct = default)
    {
        return await db.PipelineArtifacts
            .AsNoTracking()
            .Where(a => a.PipelineRunId == runId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task AddArtifactAsync(PipelineArtifact artifact, CancellationToken ct = default)
    {
        db.PipelineArtifacts.Add(artifact);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<int?> GetPipelineIdForArtifactAsync(int artifactId, CancellationToken ct = default)
    {
        return await db.PipelineArtifacts.AsNoTracking()
            .Where(a => a.Id == artifactId)
            .Select(a => (int?)a.PipelineRun.PipelineId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// BACK-002: the artifact's own ProjectId is what its project grid, quota and retention read, while
    /// its download is authorized against the pipeline's current owner. Rewriting the column when the
    /// pipeline moves keeps the four on one project. Tracked, not bulk-updated, so the rows are written
    /// by the same SaveChanges as the pipeline move and roll back with it.
    /// </summary>
    public async Task<int> ReassignPipelineArtifactsAsync(int pipelineId, int projectId, CancellationToken ct = default)
    {
        var stale = await db.PipelineArtifacts
            .Where(a => a.PipelineId == pipelineId && (a.ProjectId == null || a.ProjectId != projectId))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var artifact in stale)
            artifact.ProjectId = projectId;
        return stale.Count;
    }

    /// <summary>
    /// BACK-002 follow-up: a release has no pipeline column; its publishing pipeline is the one of its
    /// <c>CreatedByPipelineRunId</c>. Its ProjectId is what the release list, retention and permissions read,
    /// so it follows the pipeline like its artifacts, tracked and written by the same SaveChanges. The
    /// unique indexes (ProjectId, Version) and (ProjectId) where Status is Deployed are respected: a release
    /// that would break one stays in its project and is counted as skipped.
    /// </summary>
    public async Task<(int Moved, int Skipped)> ReassignPipelineReleasesAsync(
        int pipelineId, int projectId, CancellationToken ct = default)
    {
        var stale = await db.Releases
            .Where(r => r.ProjectId != projectId
                && r.CreatedByPipelineRun != null && r.CreatedByPipelineRun.PipelineId == pipelineId)
            .OrderBy(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        if (stale.Count == 0) return (0, 0);

        var takenVersions = (await db.Releases.AsNoTracking()
            .Where(r => r.ProjectId == projectId).Select(r => r.Version)
            .ToListAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var destinationHasLive = await db.Releases.AsNoTracking()
            .AnyAsync(r => r.ProjectId == projectId && r.Status == ReleaseStatus.Deployed, ct).ConfigureAwait(false);

        var moved = 0;
        foreach (var release in stale)
        {
            var live = release.Status == ReleaseStatus.Deployed;
            if (takenVersions.Contains(release.Version) || (live && destinationHasLive)) continue;
            takenVersions.Add(release.Version);
            destinationHasLive |= live;
            release.ProjectId = projectId;
            moved++;
        }
        return (moved, stale.Count - moved);
    }
}
