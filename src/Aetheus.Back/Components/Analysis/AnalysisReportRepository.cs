// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

internal sealed class AnalysisReportRepository(AppDbContext db)
{
    public Task<AnalysisRunContext?> GetRunContextAsync(int runId, CancellationToken ct)
    {
        return db.PipelineRuns
            .AsNoTracking()
            .Where(run => run.Id == runId && run.Pipeline.ProjectId != null)
            .Select(run => new AnalysisRunContext(
                run.Pipeline.Project!.OrganizationId,
                run.Pipeline.ProjectId!.Value,
                run.Id,
                run.BranchName,
                run.CommitHash,
                run.Pipeline.Project.DefaultBranch,
                null,
                run.Pipeline.Project.Name,
                run.YamlSnapshot ?? run.Pipeline.YamlDefinition))
            .FirstOrDefaultAsync(ct);
    }

    public Task<bool> ArtifactBelongsToRunAsync(int artifactId, int runId, CancellationToken ct)
    {
        return db.PipelineArtifacts.AsNoTracking()
            .AnyAsync(artifact => artifact.Id == artifactId && artifact.PipelineRunId == runId, ct);
    }

    public Task<bool> DastLeaseAuthorizesReportAsync(
        string token,
        int runId,
        string targetHost,
        int targetPort,
        DateTime now,
        CancellationToken ct)
    {
        return db.DastExecutionLeases.AsNoTracking()
            .AnyAsync(lease => lease.Token == token
                && lease.PipelineRunId == runId
                && lease.TargetHost == targetHost
                && lease.TargetPort == targetPort
                && lease.ExpiresAt > now
                && lease.ExpiresAt <= lease.CreatedAt.AddHours(24), ct);
    }

    public async Task<AnalysisReportRow?> GetReportByIdentityAsync(
        int runId,
        string scannerKey,
        string? stageName,
        string? stepName,
        string contentHash,
        CancellationToken ct)
    {
        return await db.AnalysisReports.AsNoTracking()
            .Where(item => item.PipelineRunId == runId
                && item.ScannerKey == scannerKey
                && item.StageName == stageName
                && item.StepName == stepName
                && (item.PayloadHash == contentHash
                    || (item.PayloadHash == null && item.ContentHash == contentHash)))
            .SelectRows()
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public Task<AnalysisReportRow?> RecoverReportAfterWriteConflictAsync(
        int runId,
        string scannerKey,
        string? stageName,
        string? stepName,
        string contentHash,
        CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        return GetReportByIdentityAsync(
            runId, scannerKey, stageName, stepName, contentHash, ct);
    }

    public async Task<HashSet<string>> GetBaselineFingerprintsAsync(
        int projectId,
        string baselineBranch,
        int currentRunId,
        string scannerKey,
        AnalysisCategory category,
        CancellationToken ct)
    {
        var baselineReportId = await db.AnalysisReports.AsNoTracking()
            .Where(report => report.ProjectId == projectId
                && report.PipelineRunId != null
                && report.PipelineRunId != currentRunId
                && report.BranchName == baselineBranch
                && report.ScannerKey == scannerKey
                && report.Category == category
                && (report.Status == AnalysisReportStatus.Passed
                    || report.Status == AnalysisReportStatus.Failed))
            .OrderByDescending(report => report.CompletedAt)
            .ThenByDescending(report => report.Id)
            .Select(report => (int?)report.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (baselineReportId is null) return [];

        var fingerprints = await db.AnalysisFindingOccurrences.AsNoTracking()
            .Where(occurrence => occurrence.AnalysisReportId == baselineReportId.Value)
            .Select(occurrence => occurrence.AnalysisFinding.Fingerprint)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return fingerprints.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<Dictionary<string, AnalysisFinding>> GetFindingsByFingerprintsAsync(
        int projectId,
        IReadOnlyCollection<string> fingerprints,
        CancellationToken ct)
    {
        if (fingerprints.Count == 0) return [];
        // The decisions come along: a fixed finding that comes back takes its standing decision again.
        return await db.AnalysisFindings
            .Include(finding => finding.Decisions)
            .Where(finding => finding.ProjectId == projectId
                && fingerprints.Contains(finding.Fingerprint))
            .ToDictionaryAsync(finding => finding.Fingerprint, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Metrics inserted per SaveChanges. Each batch is detached once written, so the change
    /// tracker never holds more than this many of them.</summary>
    private const int MetricBatchSize = 2_000;

    /// <summary>Writes the report while retaining its metrics in memory for policy and grading.
    /// <see cref="AddMetricsAsync"/> persists the retained metric selection afterward.</summary>
    public async Task AddReportAsync(AnalysisReport report, CancellationToken ct)
    {
        var metrics = report.Metrics.ToList();
        report.Metrics.Clear();
        db.AnalysisReports.Add(report);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (metrics.Count == 0) return;
        // The report is detached first, so handing its metrics back cannot make a later SaveChanges
        // insert them.
        db.Entry(report).State = EntityState.Detached;
        foreach (var metric in metrics)
            report.Metrics.Add(metric);
    }

    public async Task AddMetricsAsync(int reportId, IReadOnlyCollection<AnalysisMetric> metrics, CancellationToken ct)
    {
        // Insert metrics in detached batches within the caller transaction to bound EF change
        // tracking for large reports.
        if (metrics.Count == 0) return;
        var autoDetectChanges = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var batch in metrics.Chunk(MetricBatchSize))
            {
                foreach (var metric in batch)
                    metric.AnalysisReportId = reportId;
                db.AnalysisMetrics.AddRange(batch);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                foreach (var metric in batch)
                    db.Entry(metric).State = EntityState.Detached;
            }
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }
}
