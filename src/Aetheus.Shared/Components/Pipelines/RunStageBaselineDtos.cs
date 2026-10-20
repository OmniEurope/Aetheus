// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>Computes stage and step baselines from successful runs of the same pipeline started
/// before the viewed run. Excluding incomplete executions prevents early termination from biasing
/// durations downward.</summary>
public sealed record RunStageBaselinesDto
{
    /// <summary>How many successful runs the figures are drawn from (at most the server's cap).</summary>
    public int SampleRuns { get; init; }

    public List<StageBaselineDto> Stages { get; init; } = [];
    public List<StepBaselineDto> Steps { get; init; } = [];
}

public sealed record StageBaselineDto
{
    public string StageName { get; init; } = string.Empty;
    public bool IsSystem { get; init; }
    public double AverageSeconds { get; init; }

    /// <summary>The stage's duration in the most recent of the sampled runs.</summary>
    public double LastSeconds { get; init; }

    /// <summary>How many of the sampled runs actually ran this stage.</summary>
    public int Samples { get; init; }
}

public sealed record StepBaselineDto
{
    public string StageName { get; init; } = string.Empty;
    public string StepName { get; init; } = string.Empty;
    public string? MatrixLeg { get; init; }
    public bool IsSystem { get; init; }
    public double AverageSeconds { get; init; }
    public double LastSeconds { get; init; }
    public int Samples { get; init; }
}
