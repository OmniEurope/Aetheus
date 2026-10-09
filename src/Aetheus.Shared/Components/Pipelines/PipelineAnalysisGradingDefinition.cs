// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

public sealed record PipelineAnalysisGradingDefinition
{
    public int Version { get; init; } = 1;
    public string? MinimumGrade { get; init; }
    public List<string> RequiredDomains { get; init; } = [];
    public List<PipelineAnalysisGradeRuleDefinition> Rules { get; init; } = [];
}

public sealed record PipelineAnalysisGradeRuleDefinition
{
    public string Key { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string? Metric { get; init; }
    public string? Category { get; init; }
    public string? Severity { get; init; }
    /// <summary><c>weighted</c>: the rule grades the weighted score of the open findings
    /// (<see cref="AnalysisFindingScore"/>) instead of a metric or one severity's count.</summary>
    public string? FindingScore { get; init; }
    public bool NewFindingsOnly { get; init; }
    public bool Required { get; init; } = true;
    public string Direction { get; init; } = string.Empty;
    public double? A { get; init; }
    public double? B { get; init; }
    public double? C { get; init; }
    public double? D { get; init; }
    public double? E { get; init; }
}
