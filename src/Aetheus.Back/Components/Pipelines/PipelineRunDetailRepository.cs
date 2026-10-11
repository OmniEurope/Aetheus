// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Recette R-484: what a run's results add to its detail, read as figures the database works out, not
/// as the result rows themselves: the canonical coverage report's figures (its per-file list stays on
/// <c>GET runs/{id}/coverage</c>, read when the Coverage tab opens), the last lint report's counts, and
/// the metric and artifact columns the page shows.
/// </summary>
public sealed record PipelineRunResultSummaries(
    PipelineCoverageSummaryDto? Coverage,
    PipelineLintSummaryDto? Lint,
    List<RunMetricDto> Metrics,
    List<PipelineArtifactDto> Artifacts);

/// <summary>Run detail loading, extracted from <see cref="PipelineCoreRepository"/> (file-size budget).</summary>
internal sealed class PipelineRunDetailRepository(AppDbContext db)
{
    /// <summary>Recette R1-2: whether any step of these stages started at all (whatever its outcome).</summary>
    public async Task<bool> DidAnyStepStartInStagesAsync(int runId, IReadOnlyCollection<string> stageNames, CancellationToken ct = default)
    {
        return await db.PipelineStepRuns
            .AsNoTracking()
            .AnyAsync(step => step.PipelineRunId == runId
                && stageNames.Contains(step.StageName)
                && step.StartedAt != null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Recette R1-3: the pipeline's most recent run, tracked, so a repeated refusal updates it.</summary>
    public async Task<PipelineRun?> GetLatestRunTrackedAsync(int pipelineId, CancellationToken ct = default)
    {
        return await db.PipelineRuns
            .Where(run => run.PipelineId == pipelineId)
            .OrderByDescending(run => run.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<PipelineRun?> GetRunDetailAsync(int runId, CancellationToken ct = default)
    {
        return await db.PipelineRuns
            .AsNoTracking()
            .IgnoreQueryFilters([ServerQueryFilters.ExcludeRetired]) // history still names a retired server
            .Include(r => r.Pipeline)
                .ThenInclude(p => p.Project)
            .Include(r => r.StepRuns.OrderBy(s => s.Order))
                .ThenInclude(s => s.Server)
            .Include(r => r.StepRuns.OrderBy(s => s.Order))
                .ThenInclude(s => s.Task)
            // Recette R-484: no result rows here. Test results (one row per test) are counted by
            // GetTestResultSummaryAsync; coverage, lint, metrics and artifacts are read as figures and
            // columns by GetRunResultSummariesAsync.
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == runId, ct).ConfigureAwait(false);
    }

    public async Task<PipelineRunResultSummaries> GetRunResultSummariesAsync(PipelineRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var runId = run.Id;

        // The canonical report (CoverageSummaryMapper.SelectCanonical's order), its figures only: the
        // per-file JSON is the heavy column and is read on demand.
        var coverage = await db.CoverageResults.AsNoTracking()
            .Where(c => c.PipelineRunId == runId)
            .OrderByDescending(c => c.LinesValid)
            .ThenByDescending(c => c.BranchesValid)
            .ThenByDescending(c => c.CreatedAt)
            .Select(c => new PipelineCoverageSummaryDto
            {
                LineRate = c.LineRate,
                BranchRate = c.BranchRate,
                LinesCovered = c.LinesCovered,
                LinesValid = c.LinesValid,
                BranchesCovered = c.BranchesCovered,
                BranchesValid = c.BranchesValid,
                RunId = c.PipelineRunId
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        // The last lint report, as GET runs/{id}/lint reads it.
        var lint = await db.LintResults.AsNoTracking()
            .Where(l => l.PipelineRunId == runId)
            .OrderByDescending(l => l.CreatedAt)
            .ThenByDescending(l => l.Id)
            .Select(l => new PipelineLintSummaryDto
            {
                Tool = l.Tool,
                ErrorCount = l.ErrorCount,
                WarningCount = l.WarningCount,
                InfoCount = l.InfoCount,
                Passed = l.ErrorCount == 0
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var metrics = await db.RunMetrics.AsNoTracking()
            .Where(m => m.PipelineRunId == runId)
            .OrderBy(m => m.Id)
            .Select(m => new RunMetricDto
            {
                Key = m.Key,
                Type = m.Type,
                Value = m.Value,
                Unit = m.Unit,
                Threshold = m.Threshold,
                StageName = m.StageName,
                StepName = m.StepName
            })
            .ToListAsync(ct).ConfigureAwait(false);

        // The artifact columns plus the run's branch, commit and repository, as the run detail always
        // sent them; no digest (the artifact page shows it).
        var branchName = run.BranchName;
        var commitHash = run.CommitHash;
        var repositoryUrl = run.RepositoryUrl;
        var artifacts = await db.PipelineArtifacts.AsNoTracking()
            .Where(a => a.PipelineRunId == runId)
            .OrderBy(a => a.Id)
            .Select(a => new PipelineArtifactDto
            {
                Id = a.Id,
                PipelineRunId = a.PipelineRunId,
                PipelineId = a.PipelineId,
                ProjectId = a.ProjectId,
                Name = a.Name,
                FilePath = a.FilePath,
                SizeBytes = a.SizeBytes,
                StageName = a.StageName,
                StepName = a.StepName,
                CreatedAt = a.CreatedAt,
                RetentionPolicy = a.RetentionPolicy,
                RetentionExpiresAt = a.RetentionExpiresAt,
                EnvironmentName = a.EnvironmentName,
                BranchName = branchName,
                CommitHash = commitHash,
                RepositoryUrl = repositoryUrl
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return new PipelineRunResultSummaries(coverage, lint, metrics, artifacts);
    }
}
