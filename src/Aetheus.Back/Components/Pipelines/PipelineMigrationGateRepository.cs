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

    public Task<bool> EnvironmentExistsAsync(int environmentId, CancellationToken ct = default) =>
        db.Environments.AsNoTracking().AnyAsync(environment => environment.Id == environmentId, ct);

    public Task<PipelineMigrationGateVerdict?> FindLatestForEnvironmentAsync(int environmentId, CancellationToken ct = default) =>
        db.PipelineMigrationGateVerdicts
            .AsNoTracking()
            .Where(verdict => db.PipelineStepRuns.Any(step =>
                step.PipelineRunId == verdict.PipelineRunId
                && step.StageName == verdict.StageName
                && step.StepName == verdict.StepName
                && db.EnvironmentServers.Any(link =>
                    link.EnvironmentId == environmentId && link.ServerId == step.ServerId)))
            .OrderByDescending(verdict => verdict.CreatedAt)
            .ThenByDescending(verdict => verdict.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The release is found through the run's own release step (its <c>RELEASE_ID</c> output), which never changes,
    /// and not through <see cref="Release.PipelineRunId"/>: a later run that records the same release (a redeploy, a
    /// rollback) overwrites that column, which would lose the first deployment that applied the migration.
    /// </summary>
    public async Task<string?> FindFirstAppliedReleaseVersionAsync(string migrationId, CancellationToken ct = default)
    {
        var runIds = await (from verdict in db.PipelineMigrationGateVerdicts.AsNoTracking()
                            join run in db.PipelineRuns.AsNoTracking() on verdict.PipelineRunId equals run.Id
                            where run.Status == PipelineStatus.Success
                                  && (verdict.Outcome == MigrationGateOutcome.Compatible || verdict.Outcome == MigrationGateOutcome.InitialCreation)
                                  && verdict.MigrationsJson.Contains(migrationId)
                            orderby run.StartedAt, run.Id
                            select run.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        if (runIds.Count == 0) return null;

        var recorded = await ReleaseIdsRecordedByRunsAsync(runIds, ct).ConfigureAwait(false);
        if (recorded.Count == 0) return null;

        var releaseIds = recorded.Select(entry => entry.ReleaseId).Distinct().ToList();
        var versions = await db.Releases.AsNoTracking()
            .Where(release => releaseIds.Contains(release.Id))
            .ToDictionaryAsync(release => release.Id, release => release.Version, ct).ConfigureAwait(false);
        // The earliest run whose recorded release still exists decides, as before.
        return runIds
            .SelectMany(runId => recorded.Where(entry => entry.RunId == runId).Select(entry => entry.ReleaseId).Order())
            .Select(releaseId => versions.GetValueOrDefault(releaseId))
            .FirstOrDefault(version => version is not null);
    }

    private const string ReleaseIdOutput = "RELEASE_ID";

    /// <summary>The release ids the successful steps of these runs published, read from their <c>RELEASE_ID</c> output.</summary>
    private async Task<List<(int RunId, int ReleaseId)>> ReleaseIdsRecordedByRunsAsync(
        IReadOnlyCollection<int> runIds, CancellationToken ct)
    {
        var outputs = await db.PipelineStepRuns.AsNoTracking()
            .Where(step => runIds.Contains(step.PipelineRunId)
                && step.Status == TaskExecutionStatus.Success
                && step.OutputVariablesJson != null
                && step.OutputVariablesJson.Contains(ReleaseIdOutput))
            .Select(step => new { step.PipelineRunId, step.OutputVariablesJson })
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. outputs
            .Select(step => (RunId: step.PipelineRunId, ReleaseId: ReleaseIdOf(step.OutputVariablesJson)))
            .Where(entry => entry.ReleaseId > 0)];
    }

    private static int ReleaseIdOf(string? outputVariablesJson) =>
        PipelineRunHelpers.DeserializeResolvedVariables(outputVariablesJson).TryGetValue(ReleaseIdOutput, out var id)
            && int.TryParse(id, out var releaseId)
            ? releaseId
            : 0;
}
