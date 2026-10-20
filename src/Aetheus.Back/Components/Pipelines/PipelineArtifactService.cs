// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging;

namespace Aetheus.Back.Components.Pipelines;

public class PipelineArtifactService(
    IPipelineQualityResultRepository repo,
    IPipelineArtifactRepository artifactRepo,
    IPipelineRunLookupReader runs,
    TimeProvider timeProvider,
    ILogger<PipelineArtifactService> logger) : IPipelineArtifactService
{
    public async Task<List<PipelineArtifactDto>> GetArtifactsAsync(int runId, CancellationToken ct = default)
    {
        var artifacts = await artifactRepo.GetArtifactsAsync(runId, ct).ConfigureAwait(false);
        return artifacts.Select(PipelineArtifactMapper.ToStandaloneDto).ToList();
    }

    public async Task<PipelineArtifactDto?> PublishArtifactAsync(int runId, PublishArtifactRequest request, CancellationToken ct = default)
    {
        var pipelineId = await runs.GetPipelineIdForRunAsync(runId, ct).ConfigureAwait(false);
        if (pipelineId is null) return null;

        var artifact = new PipelineArtifact
        {
            PipelineRunId = runId,
            PipelineId = pipelineId.Value,
            Name = request.Name,
            FilePath = request.FilePath,
            SizeBytes = request.SizeBytes,
            StageName = request.StageName,
            StepName = request.StepName
        };

        await artifactRepo.AddArtifactAsync(artifact, ct).ConfigureAwait(false);

        return new PipelineArtifactDto
        {
            Id = artifact.Id,
            PipelineRunId = artifact.PipelineRunId,
            PipelineId = artifact.PipelineId,
            ProjectId = artifact.ProjectId,
            Name = artifact.Name,
            FilePath = artifact.FilePath,
            SizeBytes = artifact.SizeBytes,
            StageName = artifact.StageName,
            StepName = artifact.StepName,
            CreatedAt = artifact.CreatedAt,
            RetentionPolicy = artifact.RetentionPolicy,
            RetentionExpiresAt = artifact.RetentionExpiresAt
        };
    }

    public async Task<List<PipelineTestResultDto>> GetTestResultsAsync(int runId, CancellationToken ct = default)
    {
        var results = await repo.GetTestResultsAsync(runId, ct).ConfigureAwait(false);
        return results.Select(t => new PipelineTestResultDto
        {
            Id = t.Id,
            PipelineRunId = t.PipelineRunId,
            StageName = t.StageName,
            StepName = t.StepName,
            TestName = t.TestName,
            TestSuite = t.TestSuite,
            Outcome = t.Outcome,
            DurationMs = t.DurationMs,
            ErrorMessage = t.ErrorMessage,
            StackTrace = t.StackTrace,
            CreatedAt = t.CreatedAt
        }).ToList();
    }

    public async Task<PipelineTestResultSummaryDto?> PublishTestResultsAsync(
        int runId, PublishTestResultsRequest request, CancellationToken ct = default)
    {
        var pipelineId = await runs.GetPipelineIdForRunAsync(runId, ct).ConfigureAwait(false);
        if (pipelineId is null) return null;

        var testResults = TestResultParser.Parse(request.XmlContent, request.Format, runId, request.StageName, request.StepName, logger);
        if (testResults.Count == 0) return null;

        await repo.AddTestResultsAsync(testResults, ct).ConfigureAwait(false);

        return new PipelineTestResultSummaryDto
        {
            TotalTests = testResults.Count,
            Passed = testResults.Count(t => t.Outcome == TestOutcome.Passed),
            Failed = testResults.Count(t => t.Outcome == TestOutcome.Failed),
            Skipped = testResults.Count(t => t.Outcome == TestOutcome.Skipped),
            Errors = testResults.Count(t => t.Outcome == TestOutcome.Error),
            TotalDurationMs = testResults.Sum(t => t.DurationMs)
        };
    }

    public async Task<PipelineCoverageSummaryDto?> GetCoverageSummaryAsync(int runId, CancellationToken ct = default)
    {
        var results = await repo.GetCoverageResultsAsync(runId, ct).ConfigureAwait(false);
        if (results.Count == 0) return null;

        return CoverageSummaryMapper.Map(CoverageSummaryMapper.SelectCanonical(results));
    }

    /// <summary> The header filters of the coverage-by-assembly grid. The assemblies are built in
    /// memory from the run's coverage reports, so the map runs over that list. The rate and line
    /// columns show a percentage and a covered/valid pair, not the stored numbers, so the grid does
    /// not offer a filter on them. </summary>
    internal static readonly GridQueryMap<CoverageAssemblyDto> CoverageAssemblyColumns = new GridQueryMap<CoverageAssemblyDto>()
        .Text("Name", assembly => assembly.Name);

    public async Task<PaginatedResult<CoverageAssemblyDto>> GetCoverageAssembliesAsync(
        int runId, PaginationRequest request, CancellationToken ct = default)
    {
        var results = await repo.GetCoverageResultsAsync(runId, ct).ConfigureAwait(false);
        if (results.Count == 0) return new PaginatedResult<CoverageAssemblyDto>();

        IEnumerable<CoverageAssemblyDto> assemblies = CoverageSummaryMapper.MapAssemblies(
            CoverageSummaryMapper.SelectCanonical(results));
        if (!string.IsNullOrWhiteSpace(request.Search))
            assemblies = assemblies.Where(assembly =>
                assembly.Name.Contains(request.Search.Trim(), StringComparison.OrdinalIgnoreCase));
        assemblies = CoverageAssemblyColumns.ApplyFilters(assemblies.AsQueryable(), request.Filters);
        assemblies = SortAssemblies(assemblies, request.SortBy, request.SortDescending);
        var totalCount = assemblies.Count();
        var (page, pageSize) = request.Normalize();
        return new PaginatedResult<CoverageAssemblyDto>
        {
            Items = assemblies.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PipelineCoverageSummaryDto?> PublishCoverageAsync(
        int runId, PublishCoverageRequest request, CancellationToken ct = default)
    {
        var pipelineId = await runs.GetPipelineIdForRunAsync(runId, ct).ConfigureAwait(false);
        if (pipelineId is null) return null;

        var result = CoverageResultParser.Parse(request.XmlContent, runId, request.StageName, request.StepName, logger);
        if (result is null) return null;

        result.CreatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repo.AddCoverageResultAsync(result, ct).ConfigureAwait(false);

        return CoverageSummaryMapper.Map(result);
    }

    public async Task<List<CoverageTrendPointDto>> GetCoverageTrendAsync(int runId, int take, CancellationToken ct = default)
    {
        var rows = await repo.GetCoverageTrendAsync(runId, Math.Clamp(take, 2, 50), ct).ConfigureAwait(false);
        return rows.Select(r => new CoverageTrendPointDto
        {
            RunId = r.RunId,
            Date = r.Date,
            LineRate = r.LineRate,
            BranchRate = r.BranchRate
        }).ToList();
    }

    public async Task<List<ComplexityTrendPointDto>> GetComplexityTrendAsync(int runId, int take, CancellationToken ct = default)
    {
        var rows = await repo.GetComplexityTrendAsync(runId, Math.Clamp(take, 2, 50), ct).ConfigureAwait(false);
        return rows.Select(r => new ComplexityTrendPointDto
        {
            RunId = r.RunId,
            Date = r.Date,
            AvgCyclomatic = r.AvgCyclomatic,
            MaxCyclomatic = r.MaxCyclomatic,
            CrapAvg = r.CrapAvg
        }).ToList();
    }

    public async Task<ProjectQualityTrendDto> GetProjectQualityTrendAsync(int projectId, int take, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 2, 50);
        var coverage = await repo.GetProjectCoverageTrendAsync(projectId, take, ct).ConfigureAwait(false);
        var complexity = await repo.GetProjectComplexityTrendAsync(projectId, take, ct).ConfigureAwait(false);
        var tests = await repo.GetProjectTestTrendAsync(projectId, take, ct).ConfigureAwait(false);

        return new ProjectQualityTrendDto
        {
            Coverage = coverage.Select(r => new CoverageTrendPointDto
            {
                RunId = r.RunId,
                Date = r.Date,
                LineRate = r.LineRate,
                BranchRate = r.BranchRate
            }).ToList(),
            Complexity = complexity.Select(r => new ComplexityTrendPointDto
            {
                RunId = r.RunId,
                Date = r.Date,
                AvgCyclomatic = r.AvgCyclomatic,
                MaxCyclomatic = r.MaxCyclomatic,
                CrapAvg = r.CrapAvg
            }).ToList(),
            Tests = tests.Select(r => new TestTrendPointDto
            {
                RunId = r.RunId,
                Date = r.Date,
                Passed = r.Passed,
                Failed = r.Failed,
                Skipped = r.Skipped
            }).ToList()
        };
    }

    public async Task<bool> PublishComplexityAsync(int runId, PublishComplexityRequest request, CancellationToken ct = default)
    {
        var pipelineId = await runs.GetPipelineIdForRunAsync(runId, ct).ConfigureAwait(false);
        if (pipelineId is null) return false;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var metrics = new List<RunMetric>
        {
            Metric(runId, "loc.total", RunMetricType.Count, request.TotalLinesOfCode, "lines", null, request.StageName, now),
            Metric(runId, "complexity.methods", RunMetricType.Count, request.TotalMethods, "methods", null, request.StageName, now),
            Metric(runId, "complexity.cyclomatic.avg", RunMetricType.Score, request.AvgCyclomatic, null, null, request.StageName, now),
            Metric(runId, "complexity.cyclomatic.max", RunMetricType.Score, request.MaxCyclomatic, null, null, request.StageName, now),
            Metric(runId, "complexity.high", RunMetricType.Count, request.HighComplexityMethods, "methods", null, request.StageName, now)
        };

        // CRAP needs coverage; derive an aggregate from avg CC and the run's line coverage when present:
        // CRAP = cc^2 * (1 - coverage)^3 + cc. Omitted (not faked) when the run has no coverage yet.
        var coverage = await repo.GetCoverageResultsAsync(runId, ct).ConfigureAwait(false);
        if (coverage.Count > 0)
        {
            var cov = Math.Clamp(CoverageSummaryMapper.SelectCanonical(coverage).LineRate, 0, 1);
            var cc = request.AvgCyclomatic;
            var crap = Math.Round(cc * cc * Math.Pow(1 - cov, 3) + cc, 2);
            metrics.Add(Metric(runId, "complexity.crap.avg", RunMetricType.Score, crap, null, null, request.StageName, now));
        }

        await repo.AddRunMetricsAsync(metrics, ct).ConfigureAwait(false);
        return true;
    }

    private static IOrderedEnumerable<CoverageAssemblyDto> SortAssemblies(
        IEnumerable<CoverageAssemblyDto> assemblies, string? sortBy, bool descending)
        => (sortBy?.Trim().ToLowerInvariant(), descending) switch
        {
            ("name", false) => assemblies.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ("name", true) => assemblies.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ("linerate", false) => assemblies.OrderBy(item => item.LineRate).ThenBy(item => item.Name),
            ("linerate", true) => assemblies.OrderByDescending(item => item.LineRate).ThenBy(item => item.Name),
            ("linesvalid", false) => assemblies.OrderBy(item => item.LinesValid).ThenBy(item => item.Name),
            ("linesvalid", true) => assemblies.OrderByDescending(item => item.LinesValid).ThenBy(item => item.Name),
            _ => assemblies.OrderBy(item => item.LineRate).ThenBy(item => item.Name)
        };

    private static RunMetric Metric(int runId, string key, RunMetricType type, double value, string? unit, double? threshold, string? stage, DateTime now) => new()
    {
        PipelineRunId = runId,
        Key = key,
        Type = type,
        Value = value,
        Unit = unit,
        Threshold = threshold,
        StageName = stage,
        CreatedAt = now
    };

    public async Task<PipelineLintSummaryDto?> GetLintSummaryAsync(int runId, CancellationToken ct = default)
    {
        var results = await repo.GetLintResultsAsync(runId, ct).ConfigureAwait(false);
        if (results.Count == 0) return null;

        return MapLintSummary(results[^1]);
    }

    public async Task<PipelineLintSummaryDto?> PublishLintAsync(
        int runId, PublishLintRequest request, CancellationToken ct = default)
    {
        var pipelineId = await runs.GetPipelineIdForRunAsync(runId, ct).ConfigureAwait(false);
        if (pipelineId is null) return null;

        var result = LintResultParser.Parse(request.SarifContent, runId, request.StageName, request.StepName, logger);
        if (result is null) return null;

        result.CreatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repo.AddLintResultAsync(result, ct).ConfigureAwait(false);

        return MapLintSummary(result);
    }

    private static PipelineLintSummaryDto MapLintSummary(Data.Entities.LintResult r) => new()
    {
        Tool = r.Tool,
        ErrorCount = r.ErrorCount,
        WarningCount = r.WarningCount,
        InfoCount = r.InfoCount,
        Passed = r.ErrorCount == 0
    };
}
