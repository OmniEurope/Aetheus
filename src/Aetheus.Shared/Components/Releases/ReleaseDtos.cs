// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Aetheus.Shared.Components.Releases;

public sealed record ReleaseDto
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string BranchName { get; init; } = string.Empty;
    public ReleaseStatus Status { get; init; }
    public DateTime DetectedAt { get; init; }
    public DateTime? PublishedAt { get; init; }
    [JsonIgnore]
    public DateTime? CreatedAt => PublishedAt is { Year: >= 2000 } published ? published
        : DetectedAt.Year >= 2000 ? DetectedAt : null;
    public DateTime? PromotedAt { get; init; }
    public DateTime? RolledBackAt { get; init; }
    /// <summary>When the retention archived the release (<see cref="ReleaseStatus.Archived"/>).</summary>
    public DateTime? ArchivedAt { get; init; }
    public int? PipelineRunId { get; init; }
    /// <summary>The outcome of <see cref="PipelineRunId"/>, the last run that recorded this release
    /// - its candidate, or a deployment of it. "Published" beside "Failed" is a deployment that
    /// failed.</summary>
    public PipelineStatus? PipelineRunStatus { get; init; }
    public int? SourcePipelineId { get; init; }
    public string? SourcePipelineName { get; init; }
    public string? Changelog { get; init; }
    public int BuildNumber { get; init; }
    public string? CommitHash { get; init; }
    public string? TagName { get; init; }
    public string? EnvironmentName { get; init; }

    // F1: the candidate's sealed assurance verdict, hydrated at publish time from the publishing run's
    // own CANDIDATE_ASSURANCE_GRADE/CANDIDATE_DEPLOYABLE/CANDIDATE_BLOCKING_TESTS output variables.
    public AnalysisGrade? AssuranceGrade { get; init; }
    public bool? Deployable { get; init; }
    public int? BlockingTestCount { get; init; }

    /// <summary>This is the release deployed just before the one production runs.</summary>
    public bool IsPreviousDeployment { get; init; }
    /// <summary>The pipeline that deployed the live release, set only when this previous deployment can
    /// be redeployed by it: it declares a <c>candidateVersion</c> and this release has retained artifacts
    /// (R-72: a missing grade is confirmed at the launch, it no longer withholds the pipeline). Null otherwise.</summary>
    public int? RedeployPipelineId { get; init; }
    /// <summary>On the live release, identifies the pipeline that restores traffic to the reserved
    /// colour. Null when no rollback pipeline or previous deployment exists.</summary>
    public int? RevertPipelineId { get; init; }

    public string? RepositoryUrl { get; init; }

    // Cross-linking (many-to-many).
    public List<ArtifactLinkDto> Artifacts { get; init; } = [];
    public List<CommitLinkDto> Commits { get; init; } = [];
    public List<BranchLinkDto> Branches { get; init; } = [];
}

public sealed record TriggerReleaseBuildRequest
{
    [Required]
    public int PipelineId { get; init; }
}

public sealed record RollbackReleaseRequest
{
    [Required]
    public int PipelineId { get; init; }

    /// <summary>Dangerous and opt-in: the selected pipeline must contain a restore-backup step and the
    /// supplied backup must already have passed a restore-check.</summary>
    public bool RestoreDatabase { get; init; }

    public int? BackupRunId { get; init; }
}

public sealed record ReleaseRollbackDto
{
    public int Id { get; init; }
    public int SourceReleaseId { get; init; }
    public int TargetReleaseId { get; init; }
    public int? PipelineRunId { get; init; }
    public int? BackupRunId { get; init; }
    public bool RestoreDatabase { get; init; }
    public RollbackStatus Status { get; init; }
    public DateTime RequestedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? FailureReason { get; init; }
}

public sealed record ReleaseRollbackPreviewDto
{
    public bool CanRollback { get; init; }
    public string? TargetVersion { get; init; }
    public DateTime? TargetPublishedAt { get; init; }
    public string? Reason { get; init; }
}

public sealed record CreateReleaseRequest
{
    [Required]
    [StringLength(100)]
    public string Version { get; init; } = string.Empty;

    [StringLength(10000)]
    public string? Changelog { get; init; }

    public int? ArtifactId { get; init; }
    public int? PipelineRunId { get; init; }
    public int? ArtifactPipelineRunId { get; init; }

    [StringLength(40)]
    public string? CommitHash { get; init; }

    [StringLength(100)]
    public string? TagName { get; init; }

    [StringLength(200)]
    public string? BranchName { get; init; }

    /// <summary>True only when the release step runs after a verified production go-live.</summary>
    public bool Deployed { get; init; }
}

public sealed record ReleaseCreatedResponse
{
    public int Id { get; init; }
    public string Version { get; init; } = string.Empty;
    public int BuildNumber { get; init; }
}

public sealed record WebhookPayload
{
    [StringLength(200)]
    public string? Ref { get; init; }

    [StringLength(200)]
    public string? RepositoryUrl { get; init; }
}
