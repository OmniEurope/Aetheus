// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.VulnerabilityWatch;

namespace Aetheus.Shared.Components.Analysis;

/// <summary>Why the platform does not prepare the fix itself and hands over the command instead (PLAN-004, lot 4, point 13).</summary>
public enum CveRemediationFallbackReason
{
    /// <summary>The match is closed, or only to verify: there is nothing proved to fix.</summary>
    MatchNotOpen,

    /// <summary>The recommendation is not a change of a file: an operating system update, no fix yet, end of life.</summary>
    NoFileToChange,

    /// <summary>No readable internal repository holds the file the match names.</summary>
    NoRepository,

    /// <summary>The repository is a mirror of an external one: a branch here would never reach it.</summary>
    ExternalMirror,

    /// <summary>A protection rule covers the branch the fix would be pushed to.</summary>
    BranchProtected,

    /// <summary>Another match of the same advisory and package is already preparing that branch.</summary>
    BranchInUse,

    /// <summary>The bump refused to edit the files: the detail says why.</summary>
    BumpRefused,

    /// <summary>The lock file needs its package manager and the project has no pipeline that runs it.</summary>
    NoRefreshPipeline,

    /// <summary>The caller may not launch that pipeline on the servers it targets.</summary>
    RunRightsMissing,

    /// <summary>The preparation started and stopped: the detail says where.</summary>
    PreparationFailed
}

/// <summary>What is left to a person when the platform does not do the change itself. The command is null when none can be stated honestly.</summary>
public sealed record CveRemediationFallbackDto
{
    public CveRemediationFallbackReason Reason { get; init; }
    public string Detail { get; init; } = string.Empty;
    public string? Command { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyList<string> Files { get; init; } = [];
}

/// <summary>The latest attempt to fix one match. <see cref="Status"/> is null when none was ever started.</summary>
public sealed record CveRemediationDto
{
    public long MatchId { get; init; }
    public VulnerabilityRemediationStatus? Status { get; init; }
    public string? FromVersion { get; init; }
    public string? TargetVersion { get; init; }

    /// <summary>The version the lock file really holds. npm and Composer may resolve above the target; null until a lock file was read.</summary>
    public string? ResolvedVersion { get; init; }

    public string? Branch { get; init; }
    public string? BaseBranch { get; init; }
    public int? PipelineRunId { get; init; }
    public int? PullRequestId { get; init; }
    public int? PullRequestNumber { get; init; }
    public string? RequestedBy { get; init; }
    public string? FailureReason { get; init; }
    public CveRemediationFallbackDto? Fallback { get; init; }
}
