// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Monitoring;

/// <summary>
/// Completes <see cref="PipelineRunDto.GateGrade"/> for a run that owns no <c>AnalysisEvaluation</c>
/// by rolling up a grade from wherever the run's own gate actually lives, in priority order:
/// <list type="number">
/// <item>the run's own sealed assurance letter (a candidate run delegates every analysis to a child
/// pipeline and publishes its verdict via the <c>CANDIDATE_ASSURANCE_GRADE</c> output variable, see
/// <c>deploy/scripts/generate-candidate-assurance-contract.mjs</c>);</item>
/// <item>the worst grade among the runs this run's own <c>type: trigger</c> steps launched (e.g. the
/// nightly orchestration pipeline has no <c>analysis_grading</c> block of its own);</item>
/// <item>for a deploy run, the grade of the candidate release it restores, matched by the
/// <c>candidateVersion</c> run parameter against <c>Release.Version</c>: the release's own
/// <c>AssuranceGrade</c>, else the grade of the run the release points to.</item>
/// </list>
/// Every query below fetches only the single scalar it needs (never the full, secret-bearing
/// <c>OutputVariables</c>/<c>ResolvedVariables</c>/<c>Parameters</c> payload) so this stays safe to run
/// against the list projection, which deliberately omits that payload.
/// </summary>
public static class PipelineRunGradeAggregation
{
    private const string AssuranceGradeVariable = "CANDIDATE_ASSURANCE_GRADE";
    private const string CandidateVersionParameter = "candidateVersion";

    public static async Task<List<PipelineRunDto>> ApplyAsync(
        AppDbContext db, List<PipelineRunDto> runs, CancellationToken ct)
    {
        var ungradedIds = runs.Where(r => r.GateGrade is null).Select(r => r.Id).ToList();
        if (ungradedIds.Count == 0) return runs;

        var ownGrades = await GetAssuranceGradesAsync(db, ungradedIds, ct).ConfigureAwait(false);

        var childIdsByRun = runs
            .Where(r => r.GateGrade is null && !ownGrades.ContainsKey(r.Id))
            .ToDictionary(
                r => r.Id,
                r => r.Steps.Where(s => s.TriggeredRunId.HasValue).Select(s => s.TriggeredRunId!.Value).ToList());
        var allChildIds = childIdsByRun.Values.SelectMany(ids => ids).Distinct().ToList();
        var childGrades = allChildIds.Count == 0
            ? new Dictionary<int, AnalysisGrade>()
            : await GetOwnGradesAsync(db, allChildIds, ct).ConfigureAwait(false);

        var stillUngraded = runs
            .Where(r => r.GateGrade is null
                && !ownGrades.ContainsKey(r.Id)
                && !(childIdsByRun.TryGetValue(r.Id, out var kids) && kids.Any(childGrades.ContainsKey)))
            .Select(r => (r.Id, r.ProjectId))
            .ToList();
        var releaseGrades = stillUngraded.Count == 0
            ? new Dictionary<int, AnalysisGrade>()
            : await GetReleaseCandidateGradesAsync(db, stillUngraded, ct).ConfigureAwait(false);

        return runs.Select(r =>
        {
            if (r.GateGrade is not null) return r;
            if (ownGrades.TryGetValue(r.Id, out var own)) return r with { GateGrade = own };
            if (childIdsByRun.TryGetValue(r.Id, out var kids))
            {
                var worst = kids.Where(childGrades.ContainsKey).Select(id => childGrades[id]).ToList();
                if (worst.Count > 0) return r with { GateGrade = worst.Max() };
            }
            if (releaseGrades.TryGetValue(r.Id, out var viaRelease)) return r with { GateGrade = viaRelease };
            return r;
        }).ToList();
    }

    /// <summary> The pipelines table shows the same grade as the run grids. A summary carries no
    /// steps, so the shell each run needs for <see cref="ApplyAsync"/> is rebuilt from two narrow
    /// queries (its own evaluations, its trigger steps) rather than by loading the runs. </summary>
    public static async Task<List<PipelineDto>> ApplyToRecentRunsAsync(
        AppDbContext db, List<PipelineDto> pipelines, CancellationToken ct)
    {
        var runIds = pipelines.SelectMany(p => p.RecentRuns).Select(r => r.Id).Distinct().ToList();
        if (runIds.Count == 0) return pipelines;

        var evaluationGrades = await db.AnalysisEvaluations
            .Where(e => e.PipelineRunId != null && runIds.Contains(e.PipelineRunId.Value) && e.Grade != null)
            .GroupBy(e => e.PipelineRunId!.Value)
            .Select(g => new { RunId = g.Key, Grade = g.Max(e => e.Grade) })
            .ToDictionaryAsync(x => x.RunId, x => x.Grade, ct).ConfigureAwait(false);
        var triggeredByRun = (await db.PipelineStepRuns
                .Where(s => runIds.Contains(s.PipelineRunId) && s.TriggeredRunId != null)
                .Select(s => new { s.PipelineRunId, s.TriggeredRunId })
                .ToListAsync(ct).ConfigureAwait(false))
            .ToLookup(s => s.PipelineRunId, s => s.TriggeredRunId);

        var shells = pipelines
            .SelectMany(p => p.RecentRuns.Select(r => new PipelineRunDto
            {
                Id = r.Id,
                ProjectId = p.ProjectId,
                GateGrade = evaluationGrades.GetValueOrDefault(r.Id),
                Steps = triggeredByRun[r.Id].Select(id => new PipelineStepRunDto { TriggeredRunId = id }).ToList()
            }))
            .DistinctBy(r => r.Id)
            .ToList();
        var grades = (await ApplyAsync(db, shells, ct).ConfigureAwait(false))
            .Where(r => r.GateGrade is not null)
            .ToDictionary(r => r.Id, r => r.GateGrade!.Value);

        return pipelines.Select(p => p with
        {
            RecentRuns = p.RecentRuns
                .Select(r => grades.TryGetValue(r.Id, out var grade) ? r with { GateGrade = grade } : r)
                .ToList()
        }).ToList();
    }

    /// <summary>A run's own grade: its evaluations if any, else its sealed assurance letter.</summary>
    private static async Task<Dictionary<int, AnalysisGrade>> GetOwnGradesAsync(
        AppDbContext db, List<int> runIds, CancellationToken ct)
    {
        var evalGrades = await db.AnalysisEvaluations
            .Where(e => e.PipelineRunId != null && runIds.Contains(e.PipelineRunId.Value) && e.Grade != null)
            .GroupBy(e => e.PipelineRunId!.Value)
            .Select(g => new { RunId = g.Key, Grade = g.Max(e => e.Grade) })
            .ToDictionaryAsync(x => x.RunId, x => x.Grade!.Value, ct).ConfigureAwait(false);

        var remaining = runIds.Except(evalGrades.Keys).ToList();
        if (remaining.Count == 0) return evalGrades;

        var assurance = await GetAssuranceGradesAsync(db, remaining, ct).ConfigureAwait(false);
        foreach (var (id, grade) in assurance)
            evalGrades[id] = grade;
        return evalGrades;
    }

    private static async Task<Dictionary<int, AnalysisGrade>> GetAssuranceGradesAsync(
        AppDbContext db, List<int> runIds, CancellationToken ct)
    {
        var rows = await db.PipelineStepRuns
            .Where(s => runIds.Contains(s.PipelineRunId)
                && s.OutputVariablesJson != null
                && s.OutputVariablesJson.Contains(AssuranceGradeVariable))
            .Select(s => new { s.PipelineRunId, s.OutputVariablesJson })
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new Dictionary<int, AnalysisGrade>();
        foreach (var row in rows)
        {
            if (result.ContainsKey(row.PipelineRunId)) continue;
            var vars = PipelineRunListRows.DeserializeResolvedVariables(row.OutputVariablesJson);
            if (vars.TryGetValue(AssuranceGradeVariable, out var letter)
                && Enum.TryParse<AnalysisGrade>(letter, ignoreCase: true, out var grade))
            {
                result[row.PipelineRunId] = grade;
            }
        }
        return result;
    }

    private static async Task<Dictionary<int, AnalysisGrade>> GetReleaseCandidateGradesAsync(
        AppDbContext db, List<(int RunId, int? ProjectId)> runs, CancellationToken ct)
    {
        var runIds = runs.Select(r => r.RunId).ToList();
        var versionRows = await db.PipelineRuns
            .Where(r => runIds.Contains(r.Id)
                && r.ParametersJson != null
                && r.ParametersJson.Contains(CandidateVersionParameter))
            .Select(r => new { r.Id, r.ParametersJson })
            .ToListAsync(ct).ConfigureAwait(false);

        var candidateVersionByRun = new Dictionary<int, string>();
        foreach (var row in versionRows)
        {
            var parameters = PipelineRunListRows.DeserializeResolvedVariables(row.ParametersJson);
            if (parameters.TryGetValue(CandidateVersionParameter, out var version) && !string.IsNullOrWhiteSpace(version))
                candidateVersionByRun[row.Id] = version;
        }
        if (candidateVersionByRun.Count == 0) return new Dictionary<int, AnalysisGrade>();

        var versions = candidateVersionByRun.Values.Distinct().ToList();
        var releases = await db.Releases
            .Where(rel => versions.Contains(rel.Version))
            .Select(rel => new { rel.ProjectId, rel.Version, rel.AssuranceGrade, rel.PipelineRunId })
            .ToListAsync(ct).ConfigureAwait(false);

        // Prefer the grade sealed on the release because PipelineRunId can point to a later
        // deployment instead of its build. Fall back to the recorded run only when no grade exists,
        // never to the asking run itself.
        var result = new Dictionary<int, AnalysisGrade>();
        var candidateRunIdByRun = new Dictionary<int, int>();
        foreach (var (runId, projectId) in runs)
        {
            if (!candidateVersionByRun.TryGetValue(runId, out var version)) continue;
            var match = releases.FirstOrDefault(rel =>
                rel.Version == version && (projectId == null || rel.ProjectId == projectId));
            if (match is null) continue;
            if (match.AssuranceGrade is { } sealedGrade)
                result[runId] = sealedGrade;
            else if (match.PipelineRunId is { } releaseRunId && releaseRunId != runId)
                candidateRunIdByRun[runId] = releaseRunId;
        }
        if (candidateRunIdByRun.Count == 0) return result;

        var candidateGrades = await GetOwnGradesAsync(
            db, candidateRunIdByRun.Values.Distinct().ToList(), ct).ConfigureAwait(false);

        foreach (var (runId, candidateRunId) in candidateRunIdByRun)
        {
            if (candidateGrades.TryGetValue(candidateRunId, out var grade))
                result[runId] = grade;
        }
        return result;
    }
}
