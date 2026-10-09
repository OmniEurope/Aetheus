// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// PLAN-013 lots 2 and 4 (R2-114, R2-115): the per-file and per-symbol metrics and the components of an
/// analysis report are kept for the « Détail des analyses » duration of its project's « Conservation des
/// données » setting (30 days in the Standard profile), then deleted in short batches; the reports of a
/// retained release and the newest report of each project and scanner keep theirs. The project totals,
/// the reports, their evaluations and grades stay forever. Run by
/// <see cref="AnalysisOperationalMonitorService"/>, on the leader backend only.
/// </summary>
public sealed class AnalysisDetailRetentionService(
    IAnalysisDetailRetentionRepository repository,
    ILogger<AnalysisDetailRetentionService> logger)
{
    /// <summary>Rows per delete statement: each one is short and holds its row locks briefly.</summary>
    internal const int BatchSize = 5_000;

    /// <summary>Reports purged together; their detail rows are deleted batch by batch.</summary>
    internal const int ReportsPerRound = 20;

    /// <summary>Delete statements per sweep, so one sweep stays bounded however large the backlog
    /// (2,000,000 rows at most); the next sweep continues.</summary>
    internal const int MaxBatchesPerSweep = 400;

    public async Task<AnalysisDetailPurgeResult> PurgeAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var result = new AnalysisDetailPurgeResult();
        var projects = await repository.GetProjectRetentionsAsync(ct).ConfigureAwait(false);
        // Projects sharing a duration are purged together: one selection per distinct duration.
        foreach (var group in projects.GroupBy(project => project.DetailRetentionDays).OrderBy(group => group.Key))
        {
            if (result.Batches >= MaxBatchesPerSweep) break;
            var projectIds = group.Select(project => project.ProjectId).ToList();
            await PurgeOlderThanAsync(nowUtc.AddDays(-group.Key), projectIds, result, ct).ConfigureAwait(false);
        }
        if (result.Metrics + result.Components > 0)
            logger.LogInformation(
                "Analysis detail retention: deleted {Metrics} metric and {Components} component rows of {Reports} reports in {Batches} batches",
                result.Metrics, result.Components, result.Reports, result.Batches);
        return result;
    }

    private async Task PurgeOlderThanAsync(
        DateTime cutoff, IReadOnlyCollection<int> projectIds, AnalysisDetailPurgeResult result, CancellationToken ct)
    {
        while (result.Batches < MaxBatchesPerSweep)
        {
            var reportIds = await repository.GetReportsWithExpiredDetailAsync(cutoff, projectIds, ReportsPerRound, ct).ConfigureAwait(false);
            if (reportIds.Count == 0) break;
            var batchesBefore = result.Batches;
            await PurgeReportsAsync(reportIds, result, ct).ConfigureAwait(false);
            result.Reports += reportIds.Count;
            // The selection said these reports hold detail; a round that deleted nothing (another
            // backend got there first) must not spin: the next sweep looks again.
            if (result.Batches == batchesBefore) break;
        }
    }

    private async Task PurgeReportsAsync(List<int> reportIds, AnalysisDetailPurgeResult result, CancellationToken ct)
    {
        while (result.Batches < MaxBatchesPerSweep)
        {
            var deleted = await repository.DeleteMetricDetailBatchAsync(reportIds, BatchSize, ct).ConfigureAwait(false);
            if (deleted == 0) break;
            result.Metrics += deleted;
            result.Batches++;
        }
        while (result.Batches < MaxBatchesPerSweep)
        {
            var deleted = await repository.DeleteComponentBatchAsync(reportIds, BatchSize, ct).ConfigureAwait(false);
            if (deleted == 0) break;
            result.Components += deleted;
            result.Batches++;
        }
    }
}
