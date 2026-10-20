// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Pipelines;

// Per-run results & metrics published by step types (test / coverage / lint / complexity), plus their
// trend points and the generic run-metric shape. Split out of PipelineDtos.cs to keep that file within
// the 600-line budget (related DTOs may share a file - see CLAUDE.md).

public sealed record PipelineTestResultDto
{
    public int Id { get; init; }
    public int PipelineRunId { get; init; }
    public string? StageName { get; init; }
    public string? StepName { get; init; }
    public string TestName { get; init; } = string.Empty;
    public string? TestSuite { get; init; }
    public TestOutcome Outcome { get; init; }
    public double DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
    public string? StackTrace { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record PipelineTestResultSummaryDto
{
    public int TotalTests { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public int Errors { get; init; }
    public double TotalDurationMs { get; init; }
}

public sealed record PipelineCoverageSummaryDto
{
    public double LineRate { get; init; }
    public double BranchRate { get; init; }
    public int LinesCovered { get; init; }
    public int LinesValid { get; init; }
    public int BranchesCovered { get; init; }
    public int BranchesValid { get; init; }

    /// <summary>The run this coverage was published on. The run detail carries the figures without
    /// <see cref="Files"/>; the Coverage tab reads them from <c>GET runs/{RunId}/coverage</c> when
    /// it opens.</summary>
    public int? RunId { get; init; }

    /// <summary>K: per-file line coverage (worst first). Empty for legacy runs captured before per-file
    /// support, and in the run detail (see <see cref="RunId"/>).</summary>
    public List<CoverageFileDto> Files { get; init; } = [];

    /// <summary>Product coverage grouped by assembly, derived from the same merged Cobertura report.</summary>
    public List<CoverageAssemblyDto> Assemblies { get; init; } = [];
}

/// <summary>Per-file line coverage entry (K). File is the Cobertura source path.</summary>
public sealed record CoverageFileDto
{
    public string Assembly { get; init; } = string.Empty;
    public string File { get; init; } = string.Empty;
    public double LineRate { get; init; }
    public int LinesCovered { get; init; }
    public int LinesValid { get; init; }
}

public sealed record CoverageAssemblyDto
{
    public string Name { get; init; } = string.Empty;
    public double LineRate { get; init; }
    public int LinesCovered { get; init; }
    public int LinesValid { get; init; }
}

/// <summary>One point of a pipeline's coverage trend across recent runs (K), oldest to newest.</summary>
public sealed record CoverageTrendPointDto
{
    public int RunId { get; init; }
    public DateTime Date { get; init; }
    public double LineRate { get; init; }
    public double BranchRate { get; init; }
}

/// <summary>S-FEAT-C4R2: a single complexity/CRAP trend point across recent runs of a pipeline.
/// CrapAvg is null for runs that recorded complexity before any coverage (CRAP needs coverage).</summary>
public sealed record ComplexityTrendPointDto
{
    public int RunId { get; init; }
    public DateTime Date { get; init; }
    public double AvgCyclomatic { get; init; }
    public double MaxCyclomatic { get; init; }
    public double? CrapAvg { get; init; }
}

/// <summary>Archived module-finalisation plan, section 4.4: one test trend point across recent project runs.</summary>
public sealed record TestTrendPointDto
{
    public int RunId { get; init; }
    public DateTime Date { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
}

/// <summary>Archived module-finalisation plan, section 4.4: project quality trends across runs.</summary>
public sealed record ProjectQualityTrendDto
{
    [MaxLength(50)]
    public List<CoverageTrendPointDto> Coverage { get; init; } = [];
    [MaxLength(50)]
    public List<TestTrendPointDto> Tests { get; init; } = [];
    [MaxLength(50)]
    public List<ComplexityTrendPointDto> Complexity { get; init; } = [];
}

/// <summary>A single generic run metric (key · type · value · unit · threshold). The shared shape
/// lets the UI render any metric family - coverage, complexity/CRAP, LOC - without a bespoke DTO each.</summary>
public sealed record RunMetricDto
{
    public string Key { get; init; } = string.Empty;
    public RunMetricType Type { get; init; }
    public double Value { get; init; }
    public string? Unit { get; init; }
    public double? Threshold { get; init; }
    public string? StageName { get; init; }
    public string? StepName { get; init; }
}

public static class CoverageUploadLimits
{
    public const int MaxRawXmlBytes = 100 * 1024 * 1024;
}

public sealed record PublishCoverageRequest
{
    [Required]
    // Legacy JSON compatibility endpoint. Current agents use the raw XML endpoint so JSON escaping
    // cannot make an otherwise accepted report breach the byte-oriented transport limit.
    [StringLength(CoverageUploadLimits.MaxRawXmlBytes)]
    public string XmlContent { get; init; } = string.Empty;

    [StringLength(200)]
    public string? StageName { get; init; }

    [StringLength(200)]
    public string? StepName { get; init; }
}

public sealed record PipelineLintSummaryDto
{
    public string? Tool { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public int InfoCount { get; init; }
    public bool Passed { get; init; }
}

public sealed record PublishLintRequest
{
    [Required]
    // 100 MB - a full-solution Cobertura/SARIF report routinely exceeds 10 MB; kept in lockstep with
    // the [RequestSizeLimit] on the coverage/lint endpoints so DTO validation never rejects before it.
    [StringLength(100 * 1024 * 1024)]
    public string SarifContent { get; init; } = string.Empty;

    [StringLength(200)]
    public string? StageName { get; init; }

    [StringLength(200)]
    public string? StepName { get; init; }
}

/// <summary>L: aggregate code-complexity metrics published by a <c>type: complexity</c> step. The agent
/// computes these over the workspace's C# sources; the backend stores them as run metrics and derives CRAP.</summary>
public sealed record PublishComplexityRequest
{
    [Range(0, 100000)]
    public double AvgCyclomatic { get; init; }

    [Range(0, 1000000)]
    public int MaxCyclomatic { get; init; }

    [Range(0, 100000000)]
    public int TotalMethods { get; init; }

    [Range(0, 100000000)]
    public int HighComplexityMethods { get; init; }

    [Range(0, 1000000000)]
    public int TotalLinesOfCode { get; init; }

    [StringLength(200)]
    public string? StageName { get; init; }
}

public sealed record PublishTestResultsRequest
{
    [Required]
    [StringLength(50)]
    public string Format { get; init; } = "junit";

    [Required]
    // 100 MB - a full-solution Cobertura/SARIF report routinely exceeds 10 MB; kept in lockstep with
    // the [RequestSizeLimit] on the coverage/lint endpoints so DTO validation never rejects before it.
    [StringLength(100 * 1024 * 1024)]
    public string XmlContent { get; init; } = string.Empty;

    [StringLength(200)]
    public string? StageName { get; init; }

    [StringLength(200)]
    public string? StepName { get; init; }
}
