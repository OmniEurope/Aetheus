// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary> What tells whether a computed run result is still the run's result. Every input of the
/// result moves one of these values: a report or an evaluation added to the run, a finding of the
/// project seen again, reopened or re-rated by a later scan (<c>LastSeenAt</c>), or decided on,
/// marked fixed or reopened when its decision expires (<c>UpdatedAt</c>). </summary>
public sealed record AnalysisRunResultStamp(
    int ProjectId,
    bool Finished,
    DateTime? FindingsSeenAt,
    DateTime? FindingsUpdatedAt,
    int? LastReportId,
    DateTime? LastEvaluatedAt);

public sealed class AnalysisRunResultRepository(AppDbContext db) : IAnalysisRunResultRepository
{
    public Task<AnalysisRunResultStamp?> GetResultStampAsync(int runId, CancellationToken ct = default) =>
        db.PipelineRuns.AsNoTracking()
            .Where(run => run.Id == runId && run.Pipeline.ProjectId != null)
            .Select(run => new AnalysisRunResultStamp(
                run.Pipeline.ProjectId!.Value,
                run.CompletedAt != null
                    && run.Status != PipelineStatus.Pending
                    && run.Status != PipelineStatus.Running
                    && run.Status != PipelineStatus.WaitingForApproval,
                db.AnalysisFindings
                    .Where(finding => finding.ProjectId == run.Pipeline.ProjectId)
                    .Max(finding => (DateTime?)finding.LastSeenAt),
                db.AnalysisFindings
                    .Where(finding => finding.ProjectId == run.Pipeline.ProjectId)
                    .Max(finding => (DateTime?)finding.UpdatedAt),
                db.AnalysisReports
                    .Where(report => report.PipelineRunId == runId)
                    .Max(report => (int?)report.Id),
                db.AnalysisEvaluations
                    .Where(evaluation => evaluation.PipelineRunId == runId)
                    .Max(evaluation => (DateTime?)evaluation.EvaluatedAt)))
            .FirstOrDefaultAsync(ct);

    public async Task<AnalysisRunFindingsPageDto> GetFindingsPageAsync(
        IReadOnlyCollection<int> runIds, AnalysisRunFindingsRequest request, int page, int pageSize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var occurrences = db.AnalysisFindingOccurrences.AsNoTracking()
            .Where(occurrence => occurrence.AnalysisReport.PipelineRunId != null
                && runIds.Contains(occurrence.AnalysisReport.PipelineRunId.Value));

        // The counts of the whole set (the Gate tab's figures), whatever the list below is filtered on.
        var counts = await CountFindingsAsync(occurrences, ct).ConfigureAwait(false);
        var openByCategory = await CountOpenFindingsByCategoryAsync(occurrences, ct).ConfigureAwait(false);

        var listed = AnalysisRunFindingFilters.Apply(GroupFindings(AnalysisRunFindingFilters.Narrow(occurrences, request)), request);
        var total = await listed.CountAsync(ct).ConfigureAwait(false);
        var items = await AnalysisRunFindingFilters.Sort(listed, request)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        return new AnalysisRunFindingsPageDto
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
            OpenCount = counts.Open,
            NewOpenCount = counts.NewOpen,
            DecidedCount = counts.Decided,
            OpenCountsByCategory = openByCategory
        };
    }

    /// <summary>
    /// The open, new open and decided findings the occurrences point at, counted by the database: a
    /// finding counts once however many occurrences (or runs) observed it.
    /// </summary>
    internal static async Task<(int Open, int NewOpen, int Decided)> CountFindingsAsync(
        IQueryable<AnalysisFindingOccurrence> occurrences, CancellationToken ct)
    {
        var counts = await occurrences
            .GroupBy(occurrence => 1)
            .Select(group => new
            {
                Open = group.Where(occurrence => occurrence.AnalysisFinding.Status == AnalysisFindingStatus.Open)
                    .Select(occurrence => occurrence.AnalysisFindingId).Distinct().Count(),
                NewOpen = group.Where(occurrence => occurrence.IsNew
                        && occurrence.AnalysisFinding.Status == AnalysisFindingStatus.Open)
                    .Select(occurrence => occurrence.AnalysisFindingId).Distinct().Count(),
                // Only what a reviewer decided; a fixed finding is no decision (recette R1-35).
                Decided = group.Where(occurrence => occurrence.AnalysisFinding.Status == AnalysisFindingStatus.Accepted
                        || occurrence.AnalysisFinding.Status == AnalysisFindingStatus.FalsePositive
                        || occurrence.AnalysisFinding.Status == AnalysisFindingStatus.Mitigated)
                    .Select(occurrence => occurrence.AnalysisFindingId).Distinct().Count()
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return counts is null ? (0, 0, 0) : (counts.Open, counts.NewOpen, counts.Decided);
    }

    /// <summary>The open findings the occurrences point at by category, counted by the database (recette R-74): a
    /// finding counts once, and it has one category, so the counts add up to the open count.</summary>
    internal static async Task<List<AnalysisRunGateCategoryCountDto>> CountOpenFindingsByCategoryAsync(
        IQueryable<AnalysisFindingOccurrence> occurrences, CancellationToken ct) =>
        await occurrences
            .Where(occurrence => occurrence.AnalysisFinding.Status == AnalysisFindingStatus.Open)
            .GroupBy(occurrence => occurrence.AnalysisFinding.Category)
            .Select(group => new AnalysisRunGateCategoryCountDto
            {
                Category = group.Key,
                Count = group.Select(occurrence => occurrence.AnalysisFindingId).Distinct().Count()
            })
            .OrderBy(count => count.Category)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// The occurrences grouped into one row per finding, as a run sees it: its latest occurrence's file
    /// and line, and new when any occurrence was. Shared with the run gate so both read the same row.
    /// </summary>
    internal static IQueryable<AnalysisRunGateFindingDto> GroupFindings(IQueryable<AnalysisFindingOccurrence> occurrences) =>
        occurrences
            .GroupBy(occurrence => new
            {
                occurrence.AnalysisFindingId,
                occurrence.AnalysisFinding.RuleId,
                occurrence.AnalysisFinding.Title,
                occurrence.AnalysisFinding.Message,
                occurrence.AnalysisFinding.Category,
                occurrence.AnalysisFinding.Status,
                occurrence.AnalysisFinding.Severity
            })
            .Select(group => new AnalysisRunGateFindingDto
            {
                FindingId = group.Key.AnalysisFindingId,
                RuleId = group.Key.RuleId,
                Title = group.Key.Title,
                Message = group.Key.Message,
                Category = group.Key.Category,
                Severity = group.Key.Severity,
                Status = group.Key.Status,
                FilePath = group.OrderByDescending(occurrence => occurrence.CreatedAt)
                    .Select(occurrence => occurrence.FilePath)
                    .FirstOrDefault(),
                StartLine = group.OrderByDescending(occurrence => occurrence.CreatedAt)
                    .Select(occurrence => occurrence.StartLine)
                    .FirstOrDefault(),
                IsNew = group.Any(occurrence => occurrence.IsNew)
            });
}
