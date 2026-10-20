// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Git;

// Git connections.

public sealed record GitConnectionDto
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public GitProviderType ProviderType { get; init; }
    public string OwnerOrGroup { get; init; } = string.Empty;
    public string RepositoryName { get; init; } = string.Empty;
    public string? ServiceConnectionName { get; init; }
    public bool AutoSyncEnabled { get; init; }
    public DateTime? LastSyncedAt { get; init; }
    public DateTime CreatedAt { get; init; }
}

public abstract record GitRepositoryCoordinatesRequest
{
    public int ProjectId { get; init; }

    public GitProviderType ProviderType { get; init; }

    [Required]
    [StringLength(200)]
    public string OwnerOrGroup { get; init; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string RepositoryName { get; init; } = string.Empty;

    public bool AutoSyncEnabled { get; init; } = true;
}

public sealed record CreateGitConnectionRequest : GitRepositoryCoordinatesRequest
{
    public int? ServiceConnectionId { get; init; }
}

public sealed record UpdateGitConnectionRequest
{
    public int? ServiceConnectionId { get; init; }
    public bool AutoSyncEnabled { get; init; } = true;
}

// Pull requests.

public sealed record PullRequestDto
{
    public int Id { get; init; }
    public int GitConnectionId { get; init; }
    public int ExternalId { get; init; }
    [StringLength(500)]
    public string Title { get; init; } = string.Empty;
    [StringLength(4000)]
    public string? Description { get; init; }
    [StringLength(300)]
    public string SourceBranch { get; init; } = string.Empty;
    [StringLength(300)]
    public string TargetBranch { get; init; } = string.Empty;
    [StringLength(200)]
    public string AuthorLogin { get; init; } = string.Empty;
    public PullRequestStatus Status { get; init; }
    [StringLength(2048)]
    public string? ExternalUrl { get; init; }
    [StringLength(64)]
    public string? HeadCommitSha { get; init; }
    public int? LinkedPipelineRunId { get; init; }
    public DateTime ExternalCreatedAt { get; init; }
    public DateTime? ExternalMergedAt { get; init; }
    public DateTime LastSyncedAt { get; init; }
}

public sealed record PullRequestPaginationRequest : PaginationRequest
{
    public PullRequestStatus? Status { get; init; }
}

// Branch policies.

public sealed record BranchPolicyDto
{
    public int Id { get; init; }
    public int GitConnectionId { get; init; }
    public string BranchPattern { get; init; } = string.Empty;
    public BranchPolicyType PolicyType { get; init; }
    public string? ConfigurationJson { get; init; }
    public bool IsEnabled { get; init; }
    public DateTime CreatedAt { get; init; }
}

public abstract record BranchPolicyMutationRequest
{
    [Required]
    [StringLength(300)]
    public string BranchPattern { get; init; } = string.Empty;

    public BranchPolicyType PolicyType { get; init; }

    [StringLength(2000)]
    public string? ConfigurationJson { get; init; }

    public bool IsEnabled { get; init; } = true;
}

public sealed record CreateBranchPolicyRequest : BranchPolicyMutationRequest
{
    public int GitConnectionId { get; init; }
}

public sealed record UpdateBranchPolicyRequest : BranchPolicyMutationRequest;

// --- P-43: PR Status Reporting ---

public sealed record PipelineStatusReport
{
    public int PipelineRunId { get; init; }
    [StringLength(50)]
    public string State { get; init; } = string.Empty;
    [StringLength(2000)]
    public string? Description { get; init; }
    [StringLength(2048)]
    public string? TargetUrl { get; init; }
    [StringLength(200)]
    public string Context { get; init; } = "aetheus-ci";
}
