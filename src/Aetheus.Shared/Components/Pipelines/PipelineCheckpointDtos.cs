// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Pipelines;

public sealed record PipelineCheckpointResumePreviewDto
{
    public int SourceRunId { get; init; }
    public List<PipelineCheckpointResumeItemDto> Items { get; init; } = [];
}

public sealed record PipelineCheckpointResumeItemDto
{
    public string PipelineName { get; init; } = string.Empty;
    public int? RunId { get; init; }
    /// <summary>R-8: that run's number within its pipeline.</summary>
    public int? RunNumber { get; init; }
    public bool ReuseCandidate { get; init; }
    public string Reason { get; init; } = string.Empty;
}
