// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Pipelines;

public sealed record PipelineArtifactDto
{
    public int Id { get; init; }
    public int PipelineRunId { get; init; }
    public int PipelineId { get; init; }
    public int? ProjectId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public string? StageName { get; init; }
    public string? StepName { get; init; }
    public DateTime CreatedAt { get; init; }
    public ArtifactRetentionPolicy RetentionPolicy { get; init; }
    public DateTime RetentionExpiresAt { get; init; }
    public string? EnvironmentName { get; init; }
    public string? PipelineName { get; init; }
    public string? ProjectName { get; init; }

    // Git context inherited from the producing run (req. b: artifact shows commit + branch).
    public string? BranchName { get; init; }
    public string? CommitHash { get; init; }
    public string? RepositoryUrl { get; init; }
    public int? SourceRepositoryId { get; init; }

    // Cross-linking (many-to-many): an artifact can belong to several releases and relate to
    // several commits / branches.
    public List<ReleaseLinkDto> Releases { get; init; } = [];
    public List<CommitLinkDto> Commits { get; init; } = [];
    public List<BranchLinkDto> Branches { get; init; } = [];
}

public sealed record PublishArtifactRequest
{
    [Required]
    [StringLength(100)]
    public string Name { get; init; } = string.Empty;

    [Required]
    [StringLength(500)]
    public string FilePath { get; init; } = string.Empty;

    [Range(0, long.MaxValue)]
    public long SizeBytes { get; init; }

    [StringLength(200)]
    public string? StageName { get; init; }
    [StringLength(200)]
    public string? StepName { get; init; }
}

public sealed record PromoteArtifactRequest
{
    [StringLength(100)]
    public string? EnvironmentName { get; init; }
}

public sealed record ProjectArtifactsRequest : PaginationRequest
{
    public ArtifactRetentionPolicy? Policy { get; init; }
    public int? PipelineId { get; init; }
}

/// <summary> The values the project artifacts grid's checkable column filters offer. The grid is
/// loaded page by page, so the names present across the project's artifacts come from the API
/// rather than from the rows on screen. </summary>
public sealed record ProjectArtifactFilterValuesDto
{
    public List<string> PipelineNames { get; init; } = [];
    public List<string> EnvironmentNames { get; init; } = [];
}
