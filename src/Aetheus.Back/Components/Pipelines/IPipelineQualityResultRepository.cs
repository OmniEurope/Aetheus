// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Quality results of a run: test results and their summary, coverage, lint, the coverage, complexity
/// and test trends, and the run metrics. The port of the result submission service and of the task
/// metrics handler (audit AUD-07). <see cref="IPipelineRepository"/> composes it and delegates to the
/// same queries.
/// </summary>
public interface IPipelineQualityResultRepository
{
    /// <summary>The tests of a run counted by outcome in the database; null when the run recorded
    /// none.</summary>
    Task<PipelineTestResultSummaryDto?> GetTestResultSummaryAsync(int runId, CancellationToken ct = default);

    Task<List<TestResult>> GetTestResultsAsync(int runId, CancellationToken ct = default);

    Task AddTestResultsAsync(IEnumerable<TestResult> results, CancellationToken ct = default);

    Task<List<CoverageResult>> GetCoverageResultsAsync(int runId, CancellationToken ct = default);

    Task AddCoverageResultAsync(CoverageResult result, CancellationToken ct = default);

    Task<List<LintResult>> GetLintResultsAsync(int runId, CancellationToken ct = default);

    Task AddLintResultAsync(LintResult result, CancellationToken ct = default);

    Task<List<CoverageTrendRow>> GetCoverageTrendAsync(int runId, int take, CancellationToken ct = default);

    Task<List<ComplexityTrendRow>> GetComplexityTrendAsync(int runId, int take, CancellationToken ct = default);

    Task<List<CoverageTrendRow>> GetProjectCoverageTrendAsync(int projectId, int take, CancellationToken ct = default);

    Task<List<ComplexityTrendRow>> GetProjectComplexityTrendAsync(int projectId, int take, CancellationToken ct = default);

    Task<List<TestTrendRow>> GetProjectTestTrendAsync(int projectId, int take, CancellationToken ct = default);

    Task<List<RunMetric>> GetRunMetricsAsync(int runId, CancellationToken ct = default);

    Task AddRunMetricsAsync(IEnumerable<RunMetric> metrics, CancellationToken ct = default);
}
