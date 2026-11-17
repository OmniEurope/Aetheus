// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Releases;

public sealed class ReleaseProvenanceRepository(AppDbContext db) : IReleaseProvenanceRepository
{
    public Task<ReleaseProvenanceFacts?> GetFactsAsync(int releaseId, CancellationToken ct = default) =>
        db.Releases.AsNoTracking()
            .Where(release => release.Id == releaseId)
            .Select(release => new ReleaseProvenanceFacts(
                release.Id,
                release.ProjectId,
                release.CreatedByPipelineRunId,
                release.PipelineRunId,
                release.Artifacts
                    .Select(artifact => new ReleaseDeliverableFact(artifact.Id, artifact.PipelineRunId))
                    .ToList()))
            .FirstOrDefaultAsync(ct);

    public async Task<List<int>> GetTriggeredRunIdsAsync(
        int projectId, IReadOnlyCollection<int> runIds, CancellationToken ct = default)
    {
        if (runIds.Count == 0) return [];
        return await db.PipelineStepRuns.AsNoTracking()
            .Where(step => runIds.Contains(step.PipelineRunId) && step.TriggeredRunId != null
                && db.PipelineRuns.Any(run => run.Id == step.TriggeredRunId && run.Pipeline.ProjectId == projectId))
            .Select(step => step.TriggeredRunId!.Value)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<ReleaseArtifactUseFact>> GetArtifactUsesAsync(
        int releaseId, IReadOnlyCollection<int> artifactIds, CancellationToken ct = default)
    {
        var rows = await db.PipelineRunArtifactInputs.AsNoTracking()
            .Where(input => input.ReleaseId == releaseId
                || (input.ArtifactId != null && artifactIds.Contains(input.ArtifactId.Value)))
            .Select(input => new { input.PipelineRunId, input.Kind })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(row => new ReleaseArtifactUseFact(row.PipelineRunId, row.Kind)).ToList();
    }

    public async Task<List<int>> GetRollbackRunIdsAsync(int releaseId, CancellationToken ct = default) =>
        await db.ReleaseRollbacks.AsNoTracking()
            .Where(rollback => rollback.TargetReleaseId == releaseId && rollback.PipelineRunId != null)
            .Select(rollback => rollback.PipelineRunId!.Value)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<List<ReleaseArtifactInputDto>> GetArtifactInputsOfRunsAsync(
        IReadOnlyCollection<int> runIds, CancellationToken ct = default)
    {
        if (runIds.Count == 0) return [];
        return await db.PipelineRunArtifactInputs.AsNoTracking()
            .Where(input => runIds.Contains(input.PipelineRunId))
            .OrderBy(input => input.RecordedAt).ThenBy(input => input.Id)
            .Select(input => new ReleaseArtifactInputDto
            {
                ConsumerRunId = input.PipelineRunId,
                StepName = input.StepName,
                Kind = input.Kind,
                ArtifactId = input.ArtifactId,
                ArtifactName = input.ArtifactName,
                Sha256 = input.Sha256,
                SourcePipelineRunId = input.SourcePipelineRunId,
                SourcePipelineRunNumber = db.PipelineRuns
                    .Where(run => run.Id == input.SourcePipelineRunId)
                    .Select(run => run.BuildNumber)
                    .FirstOrDefault(),
                SourceReleaseId = input.ReleaseId,
                SourceReleaseVersion = input.Release == null ? null : input.Release.Version
            })
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<ReleasePackageInputDto>> GetPackagesOfRunsAsync(
        int projectId, IReadOnlyCollection<int> runIds, CancellationToken ct = default)
    {
        if (runIds.Count == 0) return [];
        var rows = await db.AnalysisComponents.AsNoTracking()
            .Where(component => component.ProjectId == projectId
                && component.AnalysisReport.PipelineRunId != null
                && runIds.Contains(component.AnalysisReport.PipelineRunId.Value))
            .Select(component => new
            {
                component.Name,
                component.Version,
                component.PackageUrl,
                component.IsDirect,
                RunId = component.AnalysisReport.PipelineRunId!.Value,
                RunNumber = component.AnalysisReport.PipelineRun!.BuildNumber
            })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        // Several scanners, or a build and its child runs, report the same package: one row each.
        return rows
            .GroupBy(row => (row.Name, row.Version, row.PackageUrl))
            .Select(group => new ReleasePackageInputDto
            {
                Name = group.Key.Name,
                Version = group.Key.Version,
                PackageUrl = group.Key.PackageUrl,
                IsDirect = group.Any(row => row.IsDirect),
                PipelineRunId = group.Min(row => row.RunId),
                PipelineRunNumber = group.MinBy(row => row.RunId)!.RunNumber
            })
            .OrderByDescending(row => row.IsDirect)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Version, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<Dictionary<int, ReleaseRunRefDto>> GetRunRefsAsync(
        int projectId, IReadOnlyCollection<int> runIds, CancellationToken ct = default)
    {
        if (runIds.Count == 0) return [];
        return await db.PipelineRuns.AsNoTracking()
            .Where(run => runIds.Contains(run.Id) && run.Pipeline.ProjectId == projectId)
            .Select(run => new ReleaseRunRefDto
            {
                RunId = run.Id,
                BuildNumber = run.BuildNumber,
                PipelineId = run.PipelineId,
                PipelineName = run.Pipeline.Name,
                Status = run.Status,
                StartedAt = run.StartedAt
            })
            .ToDictionaryAsync(run => run.RunId, ct).ConfigureAwait(false);
    }
}
