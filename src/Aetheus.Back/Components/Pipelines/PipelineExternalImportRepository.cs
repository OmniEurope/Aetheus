// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Npgsql;

namespace Aetheus.Back.Components.Pipelines;

internal sealed class PipelineExternalImportRepository(AppDbContext db) : IPipelineExternalImportRepository
{
    public async Task<List<PipelineExternalImportRow>> GetProjectPipelinesAsync(int projectId, CancellationToken ct = default)
        => await db.Pipelines.AsNoTracking()
            .Where(pipeline => pipeline.ProjectId == projectId)
            .Select(pipeline => new PipelineExternalImportRow(
                pipeline.Id, pipeline.Name, pipeline.SourceRepositoryId, pipeline.ExternalImportPath,
                pipeline.ExternalImportSha256, pipeline.ExternalImportCommit))
            .ToListAsync(ct).ConfigureAwait(false);

    public Task<Pipeline?> FindProjectPipelineAsync(int projectId, string name, CancellationToken ct = default)
        => db.Pipelines.FirstOrDefaultAsync(pipeline => pipeline.ProjectId == projectId && pipeline.Name == name, ct);

    public async Task<bool> TryAddAsync(Pipeline pipeline, CancellationToken ct = default)
    {
        await db.Pipelines.AddAsync(pipeline, ct).ConfigureAwait(false);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(pipeline).State = EntityState.Detached;
            return false;
        }
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
