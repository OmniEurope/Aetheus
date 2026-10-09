// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// PLAN-013 lot 2: the detail rows of the analysis reports whose retention ran out. Detail is the
/// per-file and per-symbol <c>AnalysisMetrics</c> rows and every <c>AnalysisComponents</c> row; the
/// project totals (metrics without file nor symbol), the reports, their evaluations and grades are never
/// touched here.
/// </summary>
public interface IAnalysisDetailRetentionRepository
{
    /// <summary>PLAN-013 lot 4: each project and the days its analysis detail is kept.</summary>
    Task<IReadOnlyList<ProjectAnalysisRetention>> GetProjectRetentionsAsync(CancellationToken ct = default);

    /// <summary>Up to <paramref name="limit"/> reports of these projects created before
    /// <paramref name="cutoff"/> that still hold detail rows, oldest first, leaving out the reports of a
    /// retained release (one that still has an artifact) and the newest report of each project and
    /// scanner.</summary>
    Task<List<int>> GetReportsWithExpiredDetailAsync(
        DateTime cutoff, IReadOnlyCollection<int> projectIds, int limit, CancellationToken ct = default);

    /// <summary>PLAN-013 lot 5: when the oldest report of the project whose detail may go was created, null
    /// when none; the storage budget compares it with the oldest telemetry before choosing what to purge.</summary>
    Task<DateTime?> GetOldestPurgeableReportDateAsync(int projectId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Deletes at most <paramref name="batchSize"/> per-file or per-symbol metric rows of these
    /// reports, in one short statement. Returns how many.</summary>
    Task<int> DeleteMetricDetailBatchAsync(IReadOnlyCollection<int> reportIds, int batchSize, CancellationToken ct = default);

    /// <summary>Deletes at most <paramref name="batchSize"/> component rows of these reports, in one short
    /// statement. Returns how many.</summary>
    Task<int> DeleteComponentBatchAsync(IReadOnlyCollection<int> reportIds, int batchSize, CancellationToken ct = default);
}

/// <summary>A project and the « Détail des analyses » duration of its data retention.</summary>
public sealed record ProjectAnalysisRetention(int ProjectId, int DetailRetentionDays);
