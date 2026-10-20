// SPDX-License-Identifier: EUPL-1.2
using System.Linq.Expressions;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>Read-only collaborator of <see cref="AnalysisRepository"/> for the findings grid and the
/// finding page.</summary>
internal sealed class AnalysisFindingOccurrenceRepository(AppDbContext db)
{
    /// <summary>The occurrence fields a grid row or the finding page shows, with the run and the
    /// pipeline reduced to the ids and name they need. Loading the run and the pipeline whole carried
    /// their YAML documents (about 60 KB each) on every row: 11.8 MB for the hundred occurrences of the
    /// finding page (PLAN-010 lot 27). Returned instances are untracked and only these members are
    /// populated.</summary>
    private static readonly Expression<Func<AnalysisFindingOccurrence, AnalysisFindingOccurrence>> Shown =
        occurrence => new AnalysisFindingOccurrence
        {
            Id = occurrence.Id,
            AnalysisReportId = occurrence.AnalysisReportId,
            AnalysisFindingId = occurrence.AnalysisFindingId,
            ToolName = occurrence.ToolName,
            ScannerKey = occurrence.ScannerKey,
            RuleId = occurrence.RuleId,
            FilePath = occurrence.FilePath,
            StartLine = occurrence.StartLine,
            EndLine = occurrence.EndLine,
            Symbol = occurrence.Symbol,
            Message = occurrence.Message,
            BranchName = occurrence.BranchName,
            CommitHash = occurrence.CommitHash,
            IsNew = occurrence.IsNew,
            CreatedAt = occurrence.CreatedAt,
            AnalysisReport = new AnalysisReport
            {
                Id = occurrence.AnalysisReportId,
                PipelineRunId = occurrence.AnalysisReport.PipelineRunId,
                PipelineRun = occurrence.AnalysisReport.PipelineRun == null
                    ? null
                    : new PipelineRun
                    {
                        Id = occurrence.AnalysisReport.PipelineRun.Id,
                        PipelineId = occurrence.AnalysisReport.PipelineRun.PipelineId,
                        Pipeline = new Pipeline
                        {
                            Id = occurrence.AnalysisReport.PipelineRun.PipelineId,
                            Name = occurrence.AnalysisReport.PipelineRun.Pipeline.Name,
                            SourceRepositoryId = occurrence.AnalysisReport.PipelineRun.Pipeline.SourceRepositoryId
                        }
                    }
            }
        };

    /// <summary>The latest occurrence of each finding, for the findings grid.</summary>
    public async Task<List<AnalysisFindingOccurrence>> GetLatestAsync(List<int> findingIds, CancellationToken ct)
    {
        var latestIds = await db.AnalysisFindingOccurrences.AsNoTracking()
            .Where(occurrence => findingIds.Contains(occurrence.AnalysisFindingId))
            .GroupBy(occurrence => occurrence.AnalysisFindingId)
            .Select(group => group.OrderByDescending(occurrence => occurrence.CreatedAt)
                .ThenByDescending(occurrence => occurrence.Id)
                .Select(occurrence => occurrence.Id)
                .First())
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return await db.AnalysisFindingOccurrences.AsNoTracking()
            .Where(occurrence => latestIds.Contains(occurrence.Id))
            .Select(Shown)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>The newest occurrences of one finding, newest first, at most <paramref name="take"/>
    /// (clamped to 1..200), read through the (finding, created) index.</summary>
    public Task<List<AnalysisFindingOccurrence>> GetForFindingAsync(int findingId, int take, CancellationToken ct) =>
        db.AnalysisFindingOccurrences.AsNoTracking()
            .Where(occurrence => occurrence.AnalysisFindingId == findingId)
            .OrderByDescending(occurrence => occurrence.CreatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .Select(Shown)
            .ToListAsync(ct);

    /// <summary>The newest occurrence of one finding, or null.</summary>
    public Task<AnalysisFindingOccurrence?> GetLatestForFindingAsync(int findingId, CancellationToken ct) =>
        db.AnalysisFindingOccurrences.AsNoTracking()
            .Where(occurrence => occurrence.AnalysisFindingId == findingId)
            .OrderByDescending(occurrence => occurrence.CreatedAt)
            .Select(Shown)
            .FirstOrDefaultAsync(ct);
}
