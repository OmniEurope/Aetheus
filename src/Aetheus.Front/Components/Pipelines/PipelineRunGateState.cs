// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

internal sealed class PipelineRunGateState
{
    public AnalysisRunGateDto? Result { get; private set; }
    public bool HasResult => Result is not null;
    public IReadOnlyList<int> FailedRunIds { get; private set; } = [];
    public bool IsIncomplete => FailedRunIds.Count > 0;

    /// <summary>Recette R-485: the runs of the tree whose analysis the gate merges; the Gate tab pages
    /// through their findings (<c>GET api/analysis/runs/findings</c>).</summary>
    public IReadOnlyList<int> RunIds { get; private set; } = [];

    public void Clear()
    {
        Result = null;
        FailedRunIds = [];
        RunIds = [];
    }

    public async Task LoadAsync(
        ApiClient api,
        PipelineRunDto rootRun,
        IEnumerable<PipelineRunDto> childRuns,
        PipelineRunPageCache? pageCache = null,
        CancellationToken ct = default)
    {
        var runs = childRuns.Append(rootRun).DistinctBy(run => run.Id).ToList();
        var loads = await Task.WhenAll(runs.Select(run => LoadOneAsync(api, run, pageCache, ct)));
        FailedRunIds = loads.Where(load => load.Failed).Select(load => load.RunId).Order().ToList();
        var gates = loads.Where(load => load.Gate is not null && HasEvidence(load.Gate)).ToList();
        RunIds = gates.Select(load => load.RunId).Order().ToList();
        Result = Merge(rootRun.Id, gates.Select(load => load.Gate!).ToList());
        if (Result is not null)
            await RefreshCountsAsync(api, rootRun.Id, ct);
        if (Result is not null && IsIncomplete)
            Result = Result with { Status = AnalysisGateStatus.Error, Grade = null };
    }

    /// <summary>
    /// Recette R2-027: a decision reverted from the Gate tab puts its finding back among the open ones,
    /// counts included, read again for the tree's runs only (one small request, not every run's
    /// result). The verdict and the grade stay: they were evaluated when the reports were published.
    /// </summary>
    public async Task ReopenFindingAsync(
        ApiClient api, PipelineRunPageCache? pageCache = null, CancellationToken ct = default)
    {
        // The cached gates of the tree still hold the old status: a reload within their lifetime
        // would bring it back, so they are dropped whatever the merged gate holds.
        pageCache?.ForgetGates();
        if (Result is not { } gate) return;
        await RefreshCountsAsync(api, gate.PipelineRunId, ct);
    }

    /// <summary>
    /// Recette R-485: the figures of the merged gate are the database's, over the whole tree (a finding
    /// observed by several runs counts once), not a count of the findings a summary lists, which is
    /// bounded. A failed read leaves the gate incomplete rather than showing figures it could not check.
    /// </summary>
    private async Task RefreshCountsAsync(ApiClient api, int rootRunId, CancellationToken ct)
    {
        if (Result is not { } gate || RunIds.Count == 0) return;
        try
        {
            var page = await api.Analysis.GetAnalysisRunFindingsAsync(
                new AnalysisRunFindingsRequest { RunIds = [.. RunIds], Page = 1, PageSize = 1 }, ct);
            Result = gate with
            {
                FindingCount = page.OpenCount,
                NewFindingCount = page.NewOpenCount,
                DecidedFindingCount = page.DecidedCount
            };
        }
        catch (HttpRequestException)
        {
            FailedRunIds = [.. FailedRunIds.Append(rootRunId).Distinct().Order()];
            Result = gate with { Status = AnalysisGateStatus.Error, Grade = null };
        }
    }

    private static async Task<GateLoad> LoadOneAsync(
        ApiClient api,
        PipelineRunDto run,
        PipelineRunPageCache? pageCache,
        CancellationToken ct)
    {
        if (pageCache?.TryGetGate(run.Id, out var cached) == true)
            return new GateLoad(run.Id, cached, false);

        try
        {
            var gate = await api.Analysis.GetAnalysisRunGateAsync(run.Id, ct);
            if (gate is not null && HasEvidence(gate))
                pageCache?.StoreGate(run, gate);
            return new GateLoad(run.Id, gate, false);
        }
        catch (HttpRequestException) { return new GateLoad(run.Id, null, true); }
    }

    private readonly record struct GateLoad(int RunId, AnalysisRunGateDto? Gate, bool Failed);

    private static bool HasEvidence(AnalysisRunGateDto gate) =>
        gate.Reports.Count > 0
        || gate.FindingCount + gate.DecidedFindingCount > 0
        || gate.Findings.Count > 0
        || gate.Violations.Count > 0
        || gate.MissingProducers.Count > 0
        || gate.Grade is not null;

    private static AnalysisRunGateDto? Merge(int rootRunId, IReadOnlyList<AnalysisRunGateDto> gates)
    {
        if (gates.Count == 0) return null;

        var findings = gates
            .SelectMany(gate => gate.Findings)
            .GroupBy(finding => finding.FindingId)
            .Select(group => group.First() with { IsNew = group.Any(finding => finding.IsNew) })
            .OrderByDescending(finding => finding.Severity)
            .ThenBy(finding => finding.Category)
            .ThenBy(finding => finding.FindingId)
            .ToList();
        var reports = gates
            .SelectMany(gate => gate.Reports)
            .DistinctBy(report => report.ReportId)
            .OrderBy(report => report.ReportId)
            .ToList();
        var grades = gates.Select(gate => gate.Grade).OfType<AnalysisGradeSummaryDto>().ToList();

        return new AnalysisRunGateDto
        {
            PipelineRunId = rootRunId,
            Status = gates.Max(gate => gate.Status),
            ReportCount = reports.Count,
            FindingCount = findings.Count(finding => finding.Status == AnalysisFindingStatus.Open),
            NewFindingCount = findings.Count(finding => finding.IsNew && finding.Status == AnalysisFindingStatus.Open),
            DecidedFindingCount = findings.Count(finding => finding.Status != AnalysisFindingStatus.Open),
            ComponentCount = reports.Sum(report => report.ComponentCount),
            MetricCount = reports.Sum(report => report.MetricCount),
            BlockerCount = reports.Sum(report => report.BlockerCount),
            WarningCount = reports.Sum(report => report.WarningCount),
            // Recette R2-129: domain by domain, as the backend grades a run tree. Taking one run's whole
            // breakdown showed the quality run's incomplete security domain beside a security run graded A.
            Grade = grades.Count == 0 ? null : AnalysisGradeMerge.MergeRuns(grades, rootRunId),
            MissingProducers = gates.SelectMany(gate => gate.MissingProducers).Distinct().ToList(),
            Reports = reports,
            Findings = findings,
            Violations = gates.SelectMany(gate => gate.Violations)
                .DistinctBy(violation => (violation.ReportId, violation.PolicyKey, violation.TargetKind, violation.TargetKey))
                .ToList()
        };
    }
}
