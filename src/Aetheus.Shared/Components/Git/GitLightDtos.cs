// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Git;

public static class GitReference
{
    /// <summary>API sentinel requesting a revision walk across every repository branch.</summary>
    public const string AllBranches = "*";
}

// --- Repository ---

public sealed record GitLightRepoDto
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string DefaultBranch { get; init; } = "main";
    public bool IsEmpty { get; init; }
    public string? CloneUrl { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastPushAt { get; init; }

    /// <summary>True for the mirror of an external repository attached beside the project's own
    /// repository: a source a pipeline asks for by name (<c>source: repository:</c>), never the
    /// repository a pipeline definition is read from by default.</summary>
    public bool IsAdditionalSource { get; init; }
}

public sealed record CreateGitLightRepoRequest
{
    public int ProjectId { get; init; }

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty; // audit: kept set; (mutated post-construction by Components/Git/GitRepositoryCreateDialog.razor @bind)

    [StringLength(1000)]
    public string? Description { get; set; } // audit: kept set; (mutated post-construction by Components/Git/GitRepositoryCreateDialog.razor @bind)

    [StringLength(200)]
    public string? DefaultBranch { get; set; } // audit: kept set; (mutated post-construction by Components/Git/GitRepositoryCreateDialog.razor @bind)
}

public sealed record UpdateGitLightRepoRequest
{
    [StringLength(1000)]
    public string? Description { get; init; }

    [StringLength(200)]
    public string? DefaultBranch { get; init; }
}

// --- Commits ---

public sealed record GitLightCommitDto
{
    public string Sha { get; init; } = string.Empty;
    public string ShortSha { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string AuthorName { get; init; } = string.Empty;
    public string AuthorEmail { get; init; } = string.Empty;
    public DateTime AuthorDate { get; init; }
    public List<string> ParentShas { get; init; } = [];

    /// <summary>S-FEAT-G6T9: ref names decorating this commit (from <c>git log %D</c>). Branch entries
    /// are bare names (e.g. <c>main</c>, <c>origin/main</c>); tag entries keep a <c>tag:</c> prefix so
    /// the graph can render them as a distinct badge. Empty for undecorated commits.</summary>
    public List<string> RefNames { get; init; } = [];

    /// <summary>The committer date (<c>%cI</c>), the date git's <c>--since</c>/<c>--until</c>
    /// filter on and the one the commits grid shows and filters.</summary>
    public DateTime CommitDate { get; init; }

    /// <summary>The branch through which the history walk reached this commit (<c>git log
    /// --source</c>, <c>%S</c>), a short name such as <c>main</c>. A commit on several branches
    /// reads as the first one the walk met; another ref keeps its kind (<c>tags/v1</c>). Null when
    /// the walk started from a commit id.</summary>
    public string? SourceRef { get; init; }
}

/// <summary>A single commit's metadata plus its file-level diff (against its first parent, or the
/// empty tree for a root commit). Backs the commit-detail page reached by clicking a commit in the
/// git repository browser.</summary>
public sealed record GitLightCommitDetailDto
{
    public GitLightCommitDto Commit { get; init; } = new();
    public PullRequestDiffDto Diff { get; init; } = new();
}

public sealed record GitCommitMessagesRequest
{
    [MinLength(1)]
    [MaxLength(200)]
    [MaxItemStringLength(64)]
    public List<string> Shas { get; init; } = [];
}

// --- Branches ---

public sealed record GitLightBranchDto
{
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
    public string? LastCommitSha { get; init; }
    public DateTime? LastCommitDate { get; init; }
    public string? LastCommitMessage { get; init; }
}

public sealed record CreateGitLightBranchRequest
{
    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty; // audit: kept set; (mutated post-construction by Components/Git/GitBranchCreateDialog.razor @bind)

    [StringLength(200)]
    public string? StartRef { get; set; } // audit: kept set; (mutated post-construction by Components/Git/GitBranchCreateDialog.razor @bind)
}

// --- Tags ---

public sealed record GitLightTagDto
{
    public string Name { get; init; } = string.Empty;
    public string Sha { get; init; } = string.Empty;
    public string? Message { get; init; }
    public string? TaggerName { get; init; }
    public DateTime? TaggerDate { get; init; }
}

public sealed record CreateGitLightTagRequest
{
    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty; // audit: kept set; (mutated post-construction by Components/Git/GitTagCreateDialog.razor @bind)

    [StringLength(200)]
    public string? Ref { get; set; } // audit: kept set; (mutated post-construction by Components/Git/GitTagCreateDialog.razor @bind)

    [StringLength(1000)]
    public string? Message { get; set; } // audit: kept set; (mutated post-construction by Components/Git/GitTagCreateDialog.razor @bind)
}

// --- Tree & Blob ---

public sealed record GitLightTreeEntryDto
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public GitTreeEntryType Type { get; init; }
    public long? Size { get; init; }
    public string? Mode { get; init; }
}

public enum GitTreeEntryType
{
    Tree,
    Blob
}

public sealed record GitLightBlobDto
{
    public string Path { get; init; } = string.Empty;
    public string? Content { get; init; }
    public long Size { get; init; }
    public bool IsBinary { get; init; }
}

// --- Internal Pull Requests ---

public sealed record CreateInternalPullRequestRequest
{
    [Required]
    [StringLength(300)]
    public string SourceBranch { get; set; } = string.Empty; // audit: kept set; (mutated post-construction by Components/Git/GitPrCreateDialog.razor @bind)

    [Required]
    [StringLength(300)]
    public string TargetBranch { get; set; } = string.Empty; // audit: kept set; (mutated post-construction by Components/Git/GitPrCreateDialog.razor @bind)

    [Required]
    [StringLength(500)]
    public string Title { get; set; } = string.Empty; // audit: kept set; (mutated post-construction by Components/Git/GitPrCreateDialog.razor @bind)

    [StringLength(4000)]
    public string? Description { get; set; } // audit: kept set; (mutated post-construction by Components/Git/GitPrCreateDialog.razor @bind)
}

public sealed record InternalPullRequestDto
{
    public int Id { get; init; }
    public int Number { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string SourceBranch { get; init; } = string.Empty;
    public string TargetBranch { get; init; } = string.Empty;
    public string AuthorLogin { get; init; } = string.Empty;
    public PullRequestStatus Status { get; init; }
    public string? HeadCommitSha { get; init; }
    public string? MergeCommitSha { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? MergedAt { get; init; }
}

public sealed record PullRequestDiffDto
{
    public List<FileDiffDto> FileDiffs { get; init; } = [];
    public DiffStatsDto Stats { get; init; } = new();
    public bool IsTruncated { get; init; }
}

public sealed record FileDiffDto
{
    public string Path { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public string? Patch { get; init; }
}

public sealed record DiffStatsDto
{
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public int FilesChanged { get; init; }
}

// --- Branch Protection Rules ---

public sealed record BranchProtectionRuleDto
{
    public int Id { get; init; }
    public int GitInternalRepoId { get; init; }
    public string Pattern { get; init; } = string.Empty;
    public bool PreventDeletion { get; init; }
    public bool PreventForcePush { get; init; }
    public BranchProtectionLevel ProtectionLevel { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record CreateBranchProtectionRuleRequest
{
    [Required]
    [StringLength(200)]
    public string Pattern { get; init; } = string.Empty;

    public bool PreventDeletion { get; init; } = true;
    public bool PreventForcePush { get; init; } = true;
    [EnumDataType(typeof(BranchProtectionLevel))]
    public BranchProtectionLevel ProtectionLevel { get; init; }
}

public sealed record UpdateBranchProtectionRuleRequest
{
    public bool PreventDeletion { get; init; }
    public bool PreventForcePush { get; init; }
    [EnumDataType(typeof(BranchProtectionLevel))]
    public BranchProtectionLevel ProtectionLevel { get; init; }
}

// --- Blame ---

public sealed record GitLightBlameLine
{
    public int LineNumber { get; init; }
    public string Sha { get; init; } = string.Empty;
    public string ShortSha { get; init; } = string.Empty;
    public string AuthorName { get; init; } = string.Empty;
    public DateTime AuthorDate { get; init; }
    public string Line { get; init; } = string.Empty;
}
