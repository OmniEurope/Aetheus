// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

internal sealed class PipelineMigrationGateRepository(AppDbContext db) : IPipelineMigrationGateRepository
{
    public async Task AddAsync(IReadOnlyList<PipelineMigrationGateVerdict> verdicts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        if (verdicts.Count == 0) return;
        db.PipelineMigrationGateVerdicts.AddRange(verdicts);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<List<PipelineMigrationGateVerdict>> ListForRunAsync(int runId, CancellationToken ct = default) =>
        db.PipelineMigrationGateVerdicts
            .AsNoTracking()
            .Where(verdict => verdict.PipelineRunId == runId)
            .OrderBy(verdict => verdict.CreatedAt)
            .ThenBy(verdict => verdict.Id)
            .ToListAsync(ct);
}
