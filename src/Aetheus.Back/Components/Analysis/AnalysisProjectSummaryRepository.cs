// SPDX-License-Identifier: EUPL-1.2

using System.Linq.Expressions;
using Aetheus.Back.Data.Entities;

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
                // What the quality page links to, read in this same command so the summary keeps
                // its budget. The latest release follows every release list: published first,
                // newest first.
                LastAnalysisRunNumber = db.AnalysisEvaluations
                    .Where(evaluation => evaluation.ProjectId == projectId && evaluation.PipelineRunId.HasValue)
                    .OrderByDescending(evaluation => evaluation.EvaluatedAt)
                    .ThenByDescending(evaluation => evaluation.Id)
                    .Select(evaluation => (int?)evaluation.PipelineRun!.BuildNumber)
                    .FirstOrDefault(),
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
                // The application version (series, a dot, release number) the run that published the
                // latest release allocated, read in the same command.
                LatestReleaseSeries = db.PipelineRuns
                    .Where(run => run.Id == db.Releases
                        .Where(release => release.ProjectId == projectId)
                        .OrderByDescending(release => release.PublishedAt != null)
                        .ThenByDescending(release => release.PublishedAt)
                        .ThenByDescending(release => release.Id)
                        .Select(release => release.CreatedByPipelineRunId ?? release.PipelineRunId)
                        .FirstOrDefault())
                    .Select(run => run.ReleaseSeries)
                    .FirstOrDefault(),
                LatestReleaseNumber = db.PipelineRuns
                    .Where(run => run.Id == db.Releases
                        .Where(release => release.ProjectId == projectId)
                        .OrderByDescending(release => release.PublishedAt != null)
                        .ThenByDescending(release => release.PublishedAt)
                        .ThenByDescending(release => release.Id)
                        .Select(release => release.CreatedByPipelineRunId ?? release.PipelineRunId)
                        .FirstOrDefault())
                    .Select(run => run.ReleaseNumber)
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

        // The commit the grade was measured on. The grade is only known now, so this is the
        // summary's one conditional command.
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
            LastAnalysisRunNumber = aggregate?.LastAnalysisRunNumber,
            LatestReleaseId = aggregate?.LatestReleaseId,
            LatestReleaseVersion = aggregate?.LatestReleaseVersion,
            LatestReleaseApplicationVersion = ApplicationVersion(aggregate?.LatestReleaseSeries, aggregate?.LatestReleaseNumber)
        };
    }

    /// <summary>The short application version of a release ("1.5.24"); null when its run allocated no number
    /// or the code declares no series.</summary>
    private static string? ApplicationVersion(string? series, int? number) =>
        number is { } n && !string.IsNullOrWhiteSpace(series)
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{series}.{n}")
            : null;

    /// <summary>The candidate's seal publishes this output variable; a run tree carrying it is a candidate.</summary>
    internal const string CandidateSealVariable = "CANDIDATE_DEPLOYABLE";

    // A trigger chain deeper than this is not walked further up; real chains are two or three levels.
    private const int MaxChainDepth = 8;

    /// <summary>R-12, R-20: each project's grade. The overall letter is the sealed verdict of the release
    /// deployed in production (<see cref="AnalysisDeployedQualification"/>), not that of the latest candidate or
    /// nightly run; the domains are the analysis grades of the deployed release's run tree when it is among the
    /// latest graded ones, else of the tree <see cref="GetAnalysisGradesAsync"/> selects. A project with neither
    /// an analysis grade nor a sealed verdict has no entry.</summary>
    internal async Task<Dictionary<int, AnalysisGradeSummaryDto>> GetGradesAsync(
        IReadOnlyCollection<int> projectIds,
        CancellationToken ct)
    {
        if (projectIds.Count == 0) return [];

        var (deployed, openFindings) = await GetDeployedReleasesAndOpenFindingCountsAsync(projectIds, ct).ConfigureAwait(false);
        var analysis = await GetAnalysisGradesAsync(
            projectIds,
            deployed.Values.Where(release => release.PipelineRunId.HasValue)
                .ToDictionary(release => release.ProjectId, release => release.PipelineRunId!.Value),
            ct).ConfigureAwait(false);

        var lastKnown = await GetLastKnownVerdictsAsync(deployed, ct).ConfigureAwait(false);

        var result = new Dictionary<int, AnalysisGradeSummaryDto>();
        foreach (var projectId in projectIds.Distinct())
        {
            var qualified = AnalysisDeployedQualification.Qualify(
                analysis.GetValueOrDefault(projectId), deployed.GetValueOrDefault(projectId), lastKnown.GetValueOrDefault(projectId));
            if (qualified is not null)
                result[projectId] = AnalysisDeployedQualification.WithFindings(
                    qualified, openFindings.GetValueOrDefault(projectId) ?? new AnalysisGradeFindingCountsDto());
        }
        return result;
    }

    /// <summary>The release currently deployed of each project (the newest when several rows carry the Deployed status),
    /// with the run whose seal published its verdict and that run's number, and R-7: each project's open findings by
    /// severity (pipeline scans and vulnerability watch alike, the same findings the project list counts; a finding
    /// without a severity counts as info). Both are read in ONE command over the projects, so the findings counts add
    /// no command to the bounded budget of the callers. A project without finding has all-zero counts.</summary>
    private async Task<(Dictionary<int, DeployedRelease> Deployed, Dictionary<int, AnalysisGradeFindingCountsDto> Findings)>
        GetDeployedReleasesAndOpenFindingCountsAsync(IReadOnlyCollection<int> projectIds, CancellationToken ct)
    {
        var ids = projectIds.Distinct().ToArray();
        var rows = await db.Projects.AsNoTracking()
            .Where(project => ids.Contains(project.Id))
            .Select(project => new
            {
                ProjectId = project.Id,
                Critical = db.AnalysisFindings.Count(finding => finding.ProjectId == project.Id
                    && finding.Status == AnalysisFindingStatus.Open && finding.Severity == AnalysisSeverity.Critical),
                High = db.AnalysisFindings.Count(finding => finding.ProjectId == project.Id
                    && finding.Status == AnalysisFindingStatus.Open && finding.Severity == AnalysisSeverity.High),
                Medium = db.AnalysisFindings.Count(finding => finding.ProjectId == project.Id
                    && finding.Status == AnalysisFindingStatus.Open && finding.Severity == AnalysisSeverity.Medium),
                Low = db.AnalysisFindings.Count(finding => finding.ProjectId == project.Id
                    && finding.Status == AnalysisFindingStatus.Open && finding.Severity == AnalysisSeverity.Low),
                Info = db.AnalysisFindings.Count(finding => finding.ProjectId == project.Id
                    && finding.Status == AnalysisFindingStatus.Open
                    && (finding.Severity == AnalysisSeverity.Info || finding.Severity == AnalysisSeverity.None)),
                Newest = db.Releases
                    .Where(release => release.ProjectId == project.Id && release.Status == ReleaseStatus.Deployed)
                    .OrderByDescending(release => release.PublishedAt != null)
                    .ThenByDescending(release => release.PublishedAt)
                    .ThenByDescending(release => release.Id)
                    .Select(release => new ReleaseVerdictRow(
                        release.ProjectId,
                        release.Id,
                        release.Version,
                        release.CommitHash,
                        release.AssuranceGrade,
                        release.Deployable,
                        release.BlockingTestCount,
                        release.PublishedAt,
                        release.CreatedByPipelineRunId ?? release.PipelineRunId))
                    .FirstOrDefault()
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var findings = rows.ToDictionary(
            row => row.ProjectId,
            row => new AnalysisGradeFindingCountsDto
            {
                Critical = row.Critical,
                High = row.High,
                Medium = row.Medium,
                Low = row.Low,
                Info = row.Info
            });
        var newest = rows.Select(row => row.Newest).OfType<ReleaseVerdictRow>().ToList();
        if (newest.Count == 0) return ([], findings);

        var runs = await GetRunNumbersAsync(newest, ct).ConfigureAwait(false);
        var deployed = newest.ToDictionary(
            row => row.ProjectId,
            row =>
            {
                var run = row.RunId is { } runId ? runs.GetValueOrDefault(runId) : null;
                return new DeployedRelease(
                    row.ProjectId,
                    row.Id,
                    ApplicationVersion(run?.ReleaseSeries, run?.ReleaseNumber) ?? row.Version,
                    row.CommitHash,
                    row.AssuranceGrade,
                    row.Deployable,
                    row.BlockingTestCount,
                    row.RunId,
                    run?.BuildNumber);
            });
        return (deployed, findings);
    }

    /// <summary>R-12, R-20 (option B): for each project whose deployed release carries no sealed verdict, the newest
    /// sealed verdict among its other releases. Projects whose deployed release has a verdict are not read.</summary>
    private async Task<Dictionary<int, LastKnownVerdict>> GetLastKnownVerdictsAsync(
        Dictionary<int, DeployedRelease> deployed, CancellationToken ct)
    {
        var withoutVerdict = deployed.Values
            .Where(release => AnalysisDeployedQualification.VerdictGrade(release.Deployable, release.AssuranceGrade) is null)
            .ToDictionary(release => release.ProjectId, release => release.ReleaseId);
        if (withoutVerdict.Count == 0) return [];

        var projectIds = withoutVerdict.Keys.ToList();
        var rows = await QueryReleaseRowsAsync(
            release => projectIds.Contains(release.ProjectId)
                && (release.Deployable == false || release.AssuranceGrade != null),
            ct).ConfigureAwait(false);

        var newest = NewestPerProject(rows.Where(row => row.Id != withoutVerdict[row.ProjectId]));
        var runs = await GetRunNumbersAsync(newest, ct).ConfigureAwait(false);

        return newest.ToDictionary(
            row => row.ProjectId,
            row =>
            {
                var run = row.RunId is { } runId ? runs.GetValueOrDefault(runId) : null;
                return new LastKnownVerdict(
                    row.Id,
                    ApplicationVersion(run?.ReleaseSeries, run?.ReleaseNumber) ?? row.Version,
                    row.CommitHash,
                    AnalysisDeployedQualification.VerdictGrade(row.Deployable, row.AssuranceGrade)!.Value,
                    row.RunId,
                    run?.BuildNumber,
                    row.BlockingTestCount,
                    row.PublishedAt);
            });
    }

    private sealed record ReleaseVerdictRow(
        int ProjectId,
        int Id,
        string Version,
        string? CommitHash,
        AnalysisGrade? AssuranceGrade,
        bool? Deployable,
        int? BlockingTestCount,
        DateTime? PublishedAt,
        int? RunId);

    private sealed record RunNumbers(int Id, int BuildNumber, string? ReleaseSeries, int? ReleaseNumber);

    private async Task<List<ReleaseVerdictRow>> QueryReleaseRowsAsync(
        Expression<Func<Release, bool>> filter, CancellationToken ct) =>
        await db.Releases.AsNoTracking()
            .Where(filter)
            .Select(release => new ReleaseVerdictRow(
                release.ProjectId,
                release.Id,
                release.Version,
                release.CommitHash,
                release.AssuranceGrade,
                release.Deployable,
                release.BlockingTestCount,
                release.PublishedAt,
                release.CreatedByPipelineRunId ?? release.PipelineRunId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    private static List<ReleaseVerdictRow> NewestPerProject(IEnumerable<ReleaseVerdictRow> rows) =>
        [.. rows
            .GroupBy(row => row.ProjectId)
            .Select(group => group.OrderByDescending(row => row.PublishedAt).ThenByDescending(row => row.Id).First())];

    private async Task<Dictionary<int, RunNumbers>> GetRunNumbersAsync(
        IEnumerable<ReleaseVerdictRow> rows, CancellationToken ct)
    {
        var runIds = rows.Where(row => row.RunId.HasValue).Select(row => row.RunId!.Value).Distinct().ToList();
        if (runIds.Count == 0) return [];
        return await db.PipelineRuns.AsNoTracking()
            .Where(run => runIds.Contains(run.Id))
            .Select(run => new RunNumbers(run.Id, run.BuildNumber, run.ReleaseSeries, run.ReleaseNumber))
            .ToDictionaryAsync(run => run.Id, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Among the latest 20 graded runs, select the run tree of the project's deployed release when it
    /// is there (<paramref name="preferredRuns"/>), else the newest run tree carrying a candidate
    /// seal, falling back to the newest graded tree. Merge triggered runs domain by domain and
    /// expose the root run through <see cref="AnalysisGradeSummaryDto.PipelineRunId"/>. These are the analysis
    /// grades only: the overall letter of a project is set by <see cref="GetGradesAsync"/>.</summary>
    internal async Task<Dictionary<int, AnalysisGradeSummaryDto>> GetAnalysisGradesAsync(
        IReadOnlyCollection<int> projectIds,
        IReadOnlyDictionary<int, int> preferredRuns,
        CancellationToken ct)
    {
        if (projectIds.Count == 0) return [];

        var graded = await GetLatestRunGradesAsync(projectIds, ct).ConfigureAwait(false);
        if (graded.Count == 0) return [];

        var rootOf = await GetRootRunIdsAsync(
            [.. graded.Select(run => run.RunId).Concat(preferredRuns.Values)], ct).ConfigureAwait(false);
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
                && step.OutputVariablesJson.Contains(CandidateSealVariable)
                // A "run to end" run past a failure seals nothing: its seal step ran only because
                // that mode let it, after a failure that stops a normal candidate.
                && !db.PipelineStepRuns.Any(failed => failed.PipelineRunId == step.PipelineRunId
                    && failed.ContinueOnErrorForced
                    && (failed.Status == TaskExecutionStatus.Failed || failed.Status == TaskExecutionStatus.Timeout)))
            .Select(step => step.PipelineRunId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var sealedRoots = sealedRunIds.Select(runId => rootOf.GetValueOrDefault(runId, runId)).ToHashSet();

        return graded
            .GroupBy(run => run.ProjectId)
            .ToDictionary(
                project => project.Key,
                project => LatestCandidateGrade(
                    project,
                    preferredRuns.TryGetValue(project.Key, out var preferredRun) ? rootOf.GetValueOrDefault(preferredRun, preferredRun) : null,
                    rootOf,
                    rootStarts,
                    sealedRoots));
    }

    private static AnalysisGradeSummaryDto LatestCandidateGrade(
        IEnumerable<GradedRun> runs,
        int? preferredRoot,
        Dictionary<int, int> rootOf,
        Dictionary<int, DateTime> rootStarts,
        HashSet<int> sealedRoots)
    {
        var tree = runs
            .GroupBy(run => rootOf.GetValueOrDefault(run.RunId, run.RunId))
            .OrderByDescending(group => group.Key == preferredRoot)
            .ThenByDescending(group => sealedRoots.Contains(group.Key))
            .ThenByDescending(group => rootStarts.GetValueOrDefault(group.Key))
            .ThenByDescending(group => group.Key)
            .First();
        // Merge each domain using its worst measured grade so domains absent from one run remain
        // represented by other runs.
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
