// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary> Changes the automated-run owner (<c>Pipeline.CreatedByUsername</c>) of one pipeline,
/// of every pipeline of a project, or of every pipeline owned by a user. Exactly one of <see
/// cref="PipelineId"/>, <see cref="ProjectId"/> and <see cref="CurrentOwnerUsername"/> names the
/// scope. </summary>
public sealed record PipelineOwnerTransferRequest
{
    /// <summary>The new owner; must be an active user.</summary>
    [Required]
    [StringLength(50)]
    public string NewOwnerUsername { get; init; } = string.Empty;

    public int? PipelineId { get; init; }

    public int? ProjectId { get; init; }

    [StringLength(50)]
    public string? CurrentOwnerUsername { get; init; }
}

/// <summary>What a transfer did (or, for a preview, would do), pipeline by pipeline.</summary>
public sealed record PipelineOwnerTransferResultDto
{
    public string NewOwnerUsername { get; init; } = string.Empty;

    /// <summary>True for a preview: nothing was written.</summary>
    public bool Preview { get; init; }

    public List<PipelineOwnerTransferItemDto> Items { get; init; } = [];
}

/// <summary>
/// One pipeline of a transfer. <see cref="NewOwnerHoldsServerAdmin"/> answers the F-EXEC-1b question for
/// the new owner on the pipeline's target servers (default parameters): false means chained runs would
/// still be refused; null means the targets could not be resolved (<see cref="TargetsUnresolvedReason"/>).
/// </summary>
public sealed record PipelineOwnerTransferItemDto
{
    public int PipelineId { get; init; }
    public string PipelineName { get; init; } = string.Empty;
    public string? ProjectName { get; init; }
    public string? PreviousOwnerUsername { get; init; }
    public bool? NewOwnerHoldsServerAdmin { get; init; }
    public List<string> ServersMissingAdmin { get; init; } = [];
    public string? TargetsUnresolvedReason { get; init; }
}

/// <summary>A pipeline and its owner, for the user detail page and the deactivation dialogs.</summary>
public sealed record PipelineOwnershipDto
{
    public int PipelineId { get; init; }
    public string PipelineName { get; init; } = string.Empty;
    public int? ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public string? OwnerUsername { get; init; }
    public bool OwnerInactive { get; init; }
}

/// <summary>An active user a pipeline can be given to.</summary>
public sealed record PipelineOwnerCandidateDto(int Id, string Username);
