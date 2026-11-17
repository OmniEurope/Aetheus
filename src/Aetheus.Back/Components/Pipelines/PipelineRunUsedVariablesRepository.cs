// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

internal sealed class PipelineRunUsedVariablesRepository(AppDbContext db) : IPipelineRunUsedVariablesReader
{
    public async Task<PipelineRunVariableSource?> GetSourceAsync(int runId, CancellationToken ct = default) =>
        await db.PipelineRuns.AsNoTracking()
            .Where(run => run.Id == runId)
            .Select(run => new PipelineRunVariableSource(
                run.YamlSnapshot,
                run.ResolvedVariablesJson,
                run.Pipeline != null ? run.Pipeline.ProjectId : null,
                run.Pipeline != null && run.Pipeline.Project != null ? run.Pipeline.Project.Name : null))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
}
