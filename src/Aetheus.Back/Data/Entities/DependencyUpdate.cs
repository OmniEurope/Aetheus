// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.DependencyUpdates;
namespace Aetheus.Back.Data.Entities;

/// <summary>
/// One automatic update of one package of a project's repository to the version that fixes an open vulnerability
/// (PLAN-007). One row per repository, ecosystem, package and target version: a row that has a branch is never done
/// again, a refused one is evaluated again at every pass without writing anything.
/// </summary>
public class DependencyUpdate
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public int ProjectId { get; set; }
    public int GitInternalRepoId { get; set; }

    /// <summary>The match that asked for it first. Null once that match is gone.</summary>
    public long? VulnerabilityMatchId { get; set; }

    public string Ecosystem { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public string FromVersion { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public string Advisory { get; set; } = string.Empty;

    /// <summary>True when the only fix crosses a major version (recette R-2, option B). Such an update has its own branch, runs the CI and
    /// the QA, and is left to the user's review: the platform never merges, promotes nor deploys it.</summary>
    public bool IsMajorUpgrade { get; set; }

    public DependencyUpdateStatus Status { get; set; }

    /// <summary>Why it was refused or failed, or what was left undone after the branch (a pipeline missing or refused).</summary>
    public string? Reason { get; set; }

    /// <summary>The license that let the bump through, as the SBOM names it.</summary>
    public string? License { get; set; }

    /// <summary>True when <see cref="License"/> was read on the target version, false when only on the current one.</summary>
    public bool LicenseOfTargetVersion { get; set; }

    /// <summary><c>aetheus/deps/&lt;package&gt;-&lt;version&gt;</c>, set once the branch is written.</summary>
    public string? Branch { get; set; }

    /// <summary>The commit the branch was written at; the runs are found by it.</summary>
    public string? CommitSha { get; set; }

    public int? CiPipelineId { get; set; }
    public int? CiPipelineRunId { get; set; }
    public int? QaPipelineId { get; set; }
    public int? QaPipelineRunId { get; set; }

    /// <summary>The project's <c>cve-lock-refresh</c> pipeline, when the bump needs its package manager to refresh a lock file.</summary>
    public int? LockPipelineId { get; set; }

    public int? LockPipelineRunId { get; set; }

    /// <summary>The pipeline owner the lock refresh runs as (R-6, decision of 2026-11-09): only that user's rights authorize it.</summary>
    public string? LockRefreshOwner { get; set; }

    /// <summary>The lock files the run hands back, as a JSON array of paths of the repository.</summary>
    public string? LockFilesJson { get; set; }

    /// <summary>R-30: the pull request opened once the CI and the QA of the branch both succeeded (<c>PullRequest.Id</c>). Null while none was
    /// opened: not yet green, or a mirror repository, where it has to be opened on the origin.</summary>
    public int? PullRequestId { get; set; }

    /// <summary>R-30: when the step after a green CI and QA was done (pull request opened, or the origin to open it on announced) and its
    /// notification sent. Null until then; once set the step is never done again.</summary>
    public DateTime? ReadyHandledAt { get; set; }

    /// <summary>R-30: when the branch was retired after the vulnerability it fixes closed (deleted, already gone, or kept and announced).
    /// Null until then; once set the step is never done again.</summary>
    public DateTime? BranchRetiredAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Organization Organization { get; set; } = null!;
    public Project Project { get; set; } = null!;
    public GitInternalRepo GitInternalRepo { get; set; } = null!;
    public VulnerabilityMatch? VulnerabilityMatch { get; set; }
}
