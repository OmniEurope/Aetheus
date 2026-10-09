// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Analysis;

internal sealed class AnalysisProjectSummaryRepository(AppDbContext db)
{
    public async Task<AnalysisProjectSummaryDto> GetAsync(
        int projectId,
        CancellationToken ct)
    {
        // One bounded SQL projection replaces the former nine sequential aggregate commands.
        // Scalar subqueries keep the result defined for projects with no reports/findings.
        var aggregate = await db.Projects.AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => new
            {
                OpenCount = db.AnalysisFindings.Count(finding =>
                    finding.ProjectId == projectId && finding.Status == AnalysisFindingStatus.Open),
                CriticalCount = db.AnalysisFindings.Count(finding =>
                    finding.ProjectId == projectId
                    && finding.Status == AnalysisFindingStatus.Open
                    && finding.Severity == AnalysisSeverity.Critical),
                HighCount = db.AnalysisFindings.Count(finding =>
                    finding.ProjectId == projectId
                    && finding.Status == AnalysisFindingStatus.Open
                    && finding.Severity == AnalysisSeverity.High),
                MediumCount = db.AnalysisFindings.Count(finding =>
                    finding.ProjectId == projectId
                    && finding.Status == AnalysisFindingStatus.Open
                    && finding.Severity == AnalysisSeverity.Medium),
                LowCount = db.AnalysisFindings.Count(finding =>
                    finding.ProjectId == projectId
                    && finding.Status == AnalysisFindingStatus.Open
                    && finding.Severity == AnalysisSeverity.Low),
                AcceptedCount = db.AnalysisFindings.Count(finding =>
                    finding.ProjectId == projectId && finding.Status == AnalysisFindingStatus.Accepted),
                FixedCount = db.AnalysisFindings.Count(finding =>
                    finding.ProjectId == projectId && finding.Status == AnalysisFindingStatus.Fixed),
                Latest = db.AnalysisReports
                    .Where(report => report.ProjectId == projectId)
                    .Max(report => (DateTime?)report.CompletedAt),
                // Recette R-430: what the quality page links to, read in this same command so the
                // summary keeps its budget. The latest release follows every release list: published
                // first, newest first.
                LastAnalysisRunId = db.AnalysisEvaluations
                    .Where(evaluation => evaluation.ProjectId == projectId && evaluation.PipelineRunId.HasValue)
                    .OrderByDescending(evaluation => evaluation.EvaluatedAt)
                    .ThenByDescending(evaluation => evaluation.Id)
                    .Select(evaluation => evaluation.PipelineRunId)
                    .FirstOrDefault(),
                LatestReleaseId = db.Releases
                    .Where(release => release.ProjectId == projectId)
                    .OrderByDescending(release => release.PublishedAt != null)
                    .ThenByDescending(release => release.PublishedAt)
                    .ThenByDescending(release => release.Id)
                    .Select(release => (int?)release.Id)
                    .FirstOrDefault(),
                LatestReleaseVersion = db.Releases
                    .Where(release => release.ProjectId == projectId)
                    .OrderByDescending(release => release.PublishedAt != null)
                    .ThenByDescending(release => release.PublishedAt)
                    .ThenByDescending(release => release.Id)
                    .Select(release => release.Version)
                    .FirstOrDefault(),
                NewCount = db.AnalysisFindingOccurrences
                    .Where(occurrence =>
                        occurrence.AnalysisReport.ProjectId == projectId
                        && occurrence.AnalysisReport.BranchName == project.DefaultBranch
                        && occurrence.IsNew
                        && occurrence.AnalysisReportId == db.AnalysisReports
                            .Where(report => report.ProjectId == projectId
                                && report.BranchName == project.DefaultBranch
                                && report.ScannerKey == occurrence.ScannerKey
                                && report.Category == occurrence.AnalysisReport.Category
                                && (report.Status == AnalysisReportStatus.Passed
                                    || report.Status == AnalysisReportStatus.Failed))
                            .OrderByDescending(report => report.CompletedAt)
                            .ThenByDescending(report => report.Id)
                            .Select(report => report.Id)
                            .FirstOrDefault())
                    .Select(occurrence => occurrence.AnalysisFindingId)
                    .Distinct()
                    .Count()
            })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var grades = await GetGradesAsync([projectId], ct).ConfigureAwait(false);
        var grade = grades.GetValueOrDefault(projectId);

        // Recette R-430: the commit the grade was measured on. The grade is only known now, so this is
        // the summary's one conditional command.
        int? gradeCommitId = null;
        if (grade?.CommitHash is { Length: > 0 } commitHash)
        {
            gradeCommitId = await db.GitCommits.AsNoTracking()
                .Where(commit => commit.ProjectId == projectId && commit.Sha == commitHash)
                .Select(commit => (int?)commit.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        return new AnalysisProjectSummaryDto
        {
            ProjectId = projectId,
            OpenCount = aggregate?.OpenCount ?? 0,
            NewCount = aggregate?.NewCount ?? 0,
            CriticalCount = aggregate?.CriticalCount ?? 0,
            HighCount = aggregate?.HighCount ?? 0,
            MediumCount = aggregate?.MediumCount ?? 0,
            LowCount = aggregate?.LowCount ?? 0,
            AcceptedCount = aggregate?.AcceptedCount ?? 0,
            FixedCount = aggregate?.FixedCount ?? 0,
            LastAnalysisAt = aggregate?.Latest,
            Grade = grade,
            GradeCommitId = gradeCommitId,
            LastAnalysisRunId = aggregate?.LastAnalysisRunId,
            LatestReleaseId = aggregate?.LatestReleaseId,
            LatestReleaseVersion = aggregate?.LatestReleaseVersion
        };
    }

    /// <summary>The candidate's seal publishes this output variable; a run tree carrying it is a candidate.</summary>
    internal const string CandidateSealVariable = "CANDIDATE_DEPLOYABLE";

    // A trigger chain deeper than this is not walked further up; real chains are two or three levels.
    private const int MaxChainDepth = 8;

    /// <summary>
    /// Recette R2-024 (decided 2026-10-01): the grade of the project is the one of its latest candidate,
    /// read as the run page reads it: the root run with the runs its trigger steps started, merged domain
    /// by domain (R2-118, <see cref="AnalysisGradeEngine.MergeRuns"/>). It replaces the domain-by-domain mix of every recent run (R-482), where a
    /// nightly or a quality-only run could replace a domain of the delivered candidate. Among the
    /// project's 20 latest graded runs, the newest run tree that carries the candidate seal wins; a
    /// project without any candidate in that window falls back to its newest graded run tree. The
    /// summary's <see cref="AnalysisGradeSummaryDto.PipelineRunId"/> is that root run, the one to open.
    /// </summary>
    internal async Task<Dictionary<int, AnalysisGradeSummaryDto>> GetGradesAsync(
        IReadOnlyCollection<int> projectIds,
        CancellationToken ct)
    {
        if (projectIds.Count == 0) return [];

        var graded = await GetLatestRunGradesAsync(projectIds, ct).ConfigureAwait(false);
        if (graded.Count == 0) return [];

        var rootOf = await GetRootRunIdsAsync([.. graded.Select(run => run.RunId)], ct).ConfigureAwait(false);
        var rootIds = rootOf.Values.Distinct().ToList();
        var rootStarts = await db.PipelineRuns.AsNoTracking()
            .Where(run => rootIds.Contains(run.Id))
            .Select(run => new { run.Id, run.StartedAt })
            .ToDictionaryAsync(run => run.Id, run => run.StartedAt, ct)
            .ConfigureAwait(false);
        var treeRunIds = rootIds.Concat(rootOf.Keys).Distinct().ToList();
        var sealedRunIds = await db.PipelineStepRuns.AsNoTracking()
            .Where(step => treeRunIds.Contains(step.PipelineRunId)
                && step.OutputVariablesJson != null
                && step.OutputVariablesJson.Contains(CandidateSealVariable))
            .Select(step => step.PipelineRunId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var sealedRoots = sealedRunIds.Select(runId => rootOf.GetValueOrDefault(runId, runId)).ToHashSet();

        return graded
            .GroupBy(run => run.ProjectId)
            .ToDictionary(
                project => project.Key,
                project => LatestCandidateGrade(project, rootOf, rootStarts, sealedRoots));
    }

    private static AnalysisGradeSummaryDto LatestCandidateGrade(
        IEnumerable<GradedRun> runs,
        Dictionary<int, int> rootOf,
        Dictionary<int, DateTime> rootStarts,
        HashSet<int> sealedRoots)
    {
        var tree = runs
            .GroupBy(run => rootOf.GetValueOrDefault(run.RunId, run.RunId))
            .OrderByDescending(group => sealedRoots.Contains(group.Key))
            .ThenByDescending(group => rootStarts.GetValueOrDefault(group.Key))
            .ThenByDescending(group => group.Key)
            .First();
        // Recette R2-118: the tree's runs are merged domain by domain (the worst grade each domain was
        // measured with). Keeping the one run with the worst overall grade left a domain empty whenever
        // that run did not grade it: on a tie, the quality run won and the security tile read "-".
        return AnalysisGradeEngine.MergeRuns([.. tree.Select(run => run.Summary)], tree.Key);
    }

    private sealed record GradedRun(int ProjectId, int RunId, AnalysisGradeSummaryDto Summary);

    /// <summary>The grade of each of the project's 20 latest graded runs, one summary per run.</summary>
    private async Task<List<GradedRun>> GetLatestRunGradesAsync(IReadOnlyCollection<int> projectIds, CancellationToken ct)
    {
        var latestRuns = await db.AnalysisEvaluations.AsNoTracking()
            .Where(evaluation => projectIds.Contains(evaluation.ProjectId)
                && evaluation.PipelineRunId.HasValue
                && evaluation.GradeSnapshotJson != "{}")
            .GroupBy(evaluation => new
            {
                evaluation.ProjectId,
                RunId = evaluation.PipelineRunId!.Value
            })
            .Select(group => new
            {
                group.Key.ProjectId,
                group.Key.RunId,
                EvaluatedAt = group.Max(evaluation => evaluation.EvaluatedAt)
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (latestRuns.Count == 0) return [];

        var runIds = latestRuns
            .GroupBy(run => run.ProjectId)
            .SelectMany(group => group
                .OrderByDescending(run => run.EvaluatedAt)
                .Take(20))
            .Select(run => run.RunId)
            .ToArray();
        var rows = await db.AnalysisEvaluations.AsNoTracking()
            .Where(evaluation => evaluation.PipelineRunId.HasValue
                && runIds.Contains(evaluation.PipelineRunId.Value)
                && evaluation.GradeSnapshotJson != "{}")
            .Select(evaluation => new
            {
                evaluation.ProjectId,
                RunId = evaluation.PipelineRunId!.Value,
                evaluation.GradeSnapshotJson
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return [.. rows
            .GroupBy(row => (row.ProjectId, row.RunId))
            .Select(run => (run.Key, Summary: AnalysisGradeEngine.Aggregate(
                run.Select(row => row.GradeSnapshotJson), pipelineRunId: run.Key.RunId)))
            .Where(run => run.Summary is not null)
            .Select(run => new GradedRun(run.Key.ProjectId, run.Key.RunId, run.Summary!))];
    }

    /// <summary>
    /// The root of each run: the run whose <c>trigger</c> step started it, walked up to the run nobody
    /// triggered (the same parent link as the pipeline lineage). A run nobody triggered is its own root.
    /// </summary>
    private async Task<Dictionary<int, int>> GetRootRunIdsAsync(IReadOnlyCollection<int> runIds, CancellationToken ct)
    {
        var rootOf = runIds.Distinct().ToDictionary(runId => runId, runId => runId);
        for (var depth = 0; depth < MaxChainDepth; depth++)
        {
            var current = rootOf.Values.Distinct().ToList();
            var links = await db.PipelineStepRuns.AsNoTracking()
                .Where(step => step.TriggeredRunId.HasValue && current.Contains(step.TriggeredRunId.Value))
                .Select(step => new { Child = step.TriggeredRunId!.Value, Parent = step.PipelineRunId })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var parentOf = links
                .Where(link => link.Parent != link.Child)
                .GroupBy(link => link.Child)
                .ToDictionary(group => group.Key, group => group.Min(link => link.Parent));
            if (!MoveUp(rootOf, parentOf)) break;
        }
        return rootOf;
    }

    private static bool MoveUp(Dictionary<int, int> rootOf, Dictionary<int, int> parentOf)
    {
        var moved = false;
        foreach (var runId in rootOf.Keys.ToList())
        {
            if (!parentOf.TryGetValue(rootOf[runId], out var parent)) continue;
            rootOf[runId] = parent;
            moved = true;
        }
        return moved;
    }
}

internal sealed class ProjectAnalysisGradeRepository(AppDbContext db) : IProjectAnalysisGradeReader
{
    private readonly AnalysisProjectSummaryRepository _summary = new(db);

    public Task<Dictionary<int, AnalysisGradeSummaryDto>> GetGradesAsync(
        IReadOnlyCollection<int> projectIds,
        CancellationToken ct = default) =>
        _summary.GetGradesAsync(projectIds, ct);
}
