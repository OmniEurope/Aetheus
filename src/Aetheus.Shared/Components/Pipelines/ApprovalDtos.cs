// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Pipelines;

public sealed record PipelineApprovalDto
{
    public int Id { get; init; }
    public int PipelineRunId { get; init; }
    /// <summary>R-8: that run's number within its pipeline.</summary>
    public int PipelineRunNumber { get; init; }
    public string StageName { get; init; } = string.Empty;
    public ApprovalScope Scope { get; init; }
    /// <summary>The stage's environment, when it has one; null for an environment-less pipeline approval.</summary>
    public int? EnvironmentId { get; init; }
    public string? EnvironmentName { get; init; }
    /// <summary>The environment's approval instructions, shown to the person deciding an
    /// environment approval; null for a pipeline approval or an environment without
    /// instructions.</summary>
    public string? EnvironmentInstructions { get; init; }
    public ApprovalStatus Status { get; init; }
    public DateTime RequestedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public int? ResolvedByUserId { get; init; }
    public string? ResolvedByUsername { get; init; }
    public string? Comments { get; init; }

    /// <summary>R-34: for a pending approval of a deployment run, the release the run restores and what is wrong
    /// with it (older than the source head, already in production). Null when the run names no release, and on
    /// an approval already decided.</summary>
    public PipelineRunDeployedReleaseDto? DeployedRelease { get; init; }
}

public sealed record ApprovalDecisionRequest
{
    [Required]
    public ApprovalStatus Decision { get; init; }

    [StringLength(500)]
    public string? Comments { get; init; }
}

/// <summary>An approval nobody has decided yet, for the home page and the top bar.</summary>
public sealed record PendingApprovalDto
{
    public int ApprovalId { get; init; }
    public int PipelineRunId { get; init; }
    /// <summary>R-8: that run's number within its pipeline.</summary>
    public int PipelineRunNumber { get; init; }
    public int PipelineId { get; init; }
    public string PipelineName { get; init; } = string.Empty;
    public int? ProjectId { get; init; }
    public string StageName { get; init; } = string.Empty;
    public ApprovalScope Scope { get; init; }
    public string? EnvironmentName { get; init; }
    public DateTime RequestedAt { get; init; }
}
