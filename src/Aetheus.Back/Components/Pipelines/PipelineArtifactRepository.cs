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
}
