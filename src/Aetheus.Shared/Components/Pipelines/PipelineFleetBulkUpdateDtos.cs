// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>R-8: "Update all pipelines" moves every pipeline of the scope pinned on an older version of a shipped
/// template (generic or versioned, R-45) to its latest version. Without a project, the scope is the caller's whole fleet.</summary>
public sealed record PipelineFleetBulkUpdateRequest
{
    [Range(1, int.MaxValue)]
    public int? ProjectId { get; init; }
}

/// <summary>What the bulk update did with one pipeline.</summary>
public enum PipelineFleetBulkUpdateOutcome
{
    Updated,
    /// <summary>The update path refused it (repository changed outside Aetheus, invalid definition, orphan
    /// overrides only a person may acknowledge): the pipeline is unchanged.</summary>
    Refused,
    /// <summary>The caller may not update this pipeline: unchanged.</summary>
    Forbidden,
    /// <summary>The pipeline changed while the update ran: unchanged, a new click takes it.</summary>
    Changed
}

public sealed record PipelineFleetBulkUpdateItemDto
{
    public int PipelineId { get; init; }
    public string PipelineName { get; init; } = string.Empty;
    public string TemplateName { get; init; } = string.Empty;
    public int FromVersion { get; init; }
    public int ToVersion { get; init; }
    public PipelineFleetBulkUpdateOutcome Outcome { get; init; }
    /// <summary>Why a refused pipeline stayed as it is, as the update path gave it.</summary>
    public string? Reason { get; init; }
}

public sealed record PipelineFleetBulkUpdateResultDto
{
    public List<PipelineFleetBulkUpdateItemDto> Items { get; init; } = [];
}
