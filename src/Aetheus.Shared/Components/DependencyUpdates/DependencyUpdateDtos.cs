// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Shared.Components.DependencyUpdates;

/// <summary>Where one automatic dependency update stands (PLAN-007). The runs keep their own status, read live.</summary>
public enum DependencyUpdateStatus
{
    /// <summary>Nothing was written: <see cref="DependencyUpdateDto.Reason"/> says why. Evaluated again at every pass.</summary>
    Refused,

    /// <summary>The branch holds the bump; the pipelines were launched, or the reason they were not is given.</summary>
    BranchPushed,

    /// <summary>The bump was planned but writing the branch failed: <see cref="DependencyUpdateDto.Reason"/> says where.</summary>
    Failed,

    /// <summary>The branch holds the manifest; the project's <c>cve-lock-refresh</c> run, launched under the pipeline's owner, refreshes
    /// the lock files. The CI starts once they are committed on the branch.</summary>
    LockRefreshing
}

/// <summary>One run an update started on its branch, with its live status. Null run id: the pipeline exists but no run was found.</summary>
public sealed record DependencyUpdateRunDto
{
    public int PipelineId { get; init; }
    public string PipelineName { get; init; } = string.Empty;
    public int? RunId { get; init; }
    public PipelineStatus? Status { get; init; }
}

/// <summary>One automatic update of one package of a project's repository, from one version to the fixed one.</summary>
public sealed record DependencyUpdateDto
{
    public int Id { get; init; }
    public string Ecosystem { get; init; } = string.Empty;
    public string PackageName { get; init; } = string.Empty;
    public string FromVersion { get; init; } = string.Empty;
    public string TargetVersion { get; init; } = string.Empty;

    /// <summary>The advisory that asked for it (a CVE id, or the advisory's own id).</summary>
    public string Advisory { get; init; } = string.Empty;

    public DependencyUpdateStatus Status { get; init; }

    /// <summary>True when the only fix crosses a major version: the update has its own branch and only the user may merge it.</summary>
    public bool IsMajorUpgrade { get; init; }

    /// <summary>True for a major update whose branch is written (not refused, not failed before the branch): the user must review it
    /// before anything is merged. Nothing automatic ever merges, promotes or deploys it.</summary>
    public bool ReviewRequired => IsMajorUpgrade && Branch is not null && Status != DependencyUpdateStatus.Refused;

    /// <summary>Why it was refused or failed, or what is left to the user (a pipeline not found, a run not started).</summary>
    public string? Reason { get; init; }

    /// <summary>The license that let it through, and whether it was read on the target version or only on the current one.</summary>
    public string? License { get; init; }
    public bool LicenseOfTargetVersion { get; init; }

    public string? Branch { get; init; }
    public int? RepositoryId { get; init; }
    public string? CommitSha { get; init; }
    public IReadOnlyList<DependencyUpdateRunDto> Runs { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

/// <summary>The project's setting and its updates, newest first.</summary>
public sealed record DependencyUpdatesOverviewDto
{
    public bool Enabled { get; init; }
    public IReadOnlyList<DependencyUpdateDto> Items { get; init; } = [];
}

/// <summary>Turns the automatic dependency updates of a project on or off.</summary>
public sealed record UpdateDependencyUpdateSettingsRequest
{
    public bool Enabled { get; init; }
}
