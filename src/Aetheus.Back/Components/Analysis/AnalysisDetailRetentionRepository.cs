// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

public sealed class AnalysisDetailRetentionRepository(AppDbContext db) : IAnalysisDetailRetentionRepository
{
    public async Task<IReadOnlyList<ProjectAnalysisRetention>> GetProjectRetentionsAsync(CancellationToken ct = default)
    {
        var rows = await db.Projects.AsNoTracking()
            .Select(project => new
            {
                project.Id,
                project.DataRetentionProfile,
                project.AnalysisDetailRetentionDays
            })
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(row => new ProjectAnalysisRetention(
            row.Id,
            DataRetentionProfiles.Resolve(row.DataRetentionProfile, new DataRetentionOverrides
            {
                AnalysisDetailRetentionDays = row.AnalysisDetailRetentionDays
            }).AnalysisDetailRetentionDays))];
    }

    public async Task<List<int>> GetReportsWithExpiredDetailAsync(
        DateTime cutoff, IReadOnlyCollection<int> projectIds, int limit, CancellationToken ct = default) =>
        await Purgeable(cutoff, projectIds)
            .OrderBy(report => report.CreatedAt)
            .ThenBy(report => report.Id)
            .Select(report => report.Id)
            .Take(limit)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<DateTime?> GetOldestPurgeableReportDateAsync(int projectId, DateTime nowUtc, CancellationToken ct = default) =>
        await Purgeable(nowUtc, [projectId])
            .OrderBy(report => report.CreatedAt)
            .Select(report => (DateTime?)report.CreatedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    /// <summary>The reports of these projects created before <paramref name="cutoff"/> whose detail may go.</summary>
    private IQueryable<AnalysisReport> Purgeable(DateTime cutoff, IReadOnlyCollection<int> projectIds) =>
        db.AnalysisReports.AsNoTracking()
            .Where(report => report.CreatedAt < cutoff
                && projectIds.Contains(report.ProjectId)
                // Still holds detail: a report already purged costs nothing more.
                && (report.Metrics.Any(metric => metric.FilePath != null || metric.Symbol != null)
                    || report.Components.Any())
                // The newest report of its project and scanner keeps its detail, so the current state of
                // a project analysed less often than the retention stays readable: only a report with a
                // newer one of the same project and scanner is purged.
                && db.AnalysisReports.Any(newer => newer.ProjectId == report.ProjectId
                    && newer.ScannerKey == report.ScannerKey
                    && (newer.CreatedAt > report.CreatedAt
                        || (newer.CreatedAt == report.CreatedAt && newer.Id > report.Id)))
                // A release still retained (its artifact is kept) keeps the detail of its analyses: the
                // run that created or recorded it, the runs of its artifacts, its commit (which survives
                // the deletion of old runs), or one of its artifacts analysed directly.
                && !db.Releases.Any(release => release.ProjectId == report.ProjectId
                    && release.Artifacts.Any()
                    && ((report.PipelineRunId != null
                            && (release.CreatedByPipelineRunId == report.PipelineRunId
                                || release.PipelineRunId == report.PipelineRunId
                                || release.Artifacts.Any(artifact => artifact.PipelineRunId == report.PipelineRunId)))
                        || (report.CommitHash != null && release.CommitHash == report.CommitHash)
                        || (report.PipelineArtifactId != null
                            && release.Artifacts.Any(artifact => artifact.Id == report.PipelineArtifactId)))));

    public async Task<int> DeleteMetricDetailBatchAsync(
        IReadOnlyCollection<int> reportIds, int batchSize, CancellationToken ct = default)
    {
        if (reportIds.Count == 0) return 0;
        var ids = await db.AnalysisMetrics.AsNoTracking()
            .Where(metric => reportIds.Contains(metric.AnalysisReportId)
                && (metric.FilePath != null || metric.Symbol != null))
            .Select(metric => metric.Id)
            .Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0) return 0;
        if (db.Database.IsRelational())
            return await db.AnalysisMetrics.Where(metric => ids.Contains(metric.Id))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var rows = await db.AnalysisMetrics.Where(metric => ids.Contains(metric.Id)).ToListAsync(ct).ConfigureAwait(false);
        db.AnalysisMetrics.RemoveRange(rows);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }

    public async Task<int> DeleteComponentBatchAsync(
        IReadOnlyCollection<int> reportIds, int batchSize, CancellationToken ct = default)
    {
        if (reportIds.Count == 0) return 0;
        var ids = await db.AnalysisComponents.AsNoTracking()
            .Where(component => reportIds.Contains(component.AnalysisReportId))
            .Select(component => component.Id)
            .Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0) return 0;
        if (db.Database.IsRelational())
            return await db.AnalysisComponents.Where(component => ids.Contains(component.Id))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var rows = await db.AnalysisComponents.Where(component => ids.Contains(component.Id)).ToListAsync(ct).ConfigureAwait(false);
        db.AnalysisComponents.RemoveRange(rows);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }
}
