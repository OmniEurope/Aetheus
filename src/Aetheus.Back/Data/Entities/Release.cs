// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Data.Entities;

public class Release
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Version { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public ReleaseStatus Status { get; set; } = ReleaseStatus.Detected;
    public DateTime DetectedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? PromotedAt { get; set; }
    public DateTime? RolledBackAt { get; set; }
    /// <summary>When the retention archived the release (decision D2); null otherwise, and cleared when
    /// a new publication of the same version makes it live again.</summary>
    public DateTime? ArchivedAt { get; set; }
    public int? PipelineRunId { get; set; }
    /// <summary>The run that first published this release. Unlike <see cref="PipelineRunId"/> (the
    /// last run that recorded it, a deployment included) it is never overwritten. Stamped by
    /// <c>ReleaseRepository</c>; null for releases published before it existed and for releases
    /// only detected from a branch.</summary>
    public int? CreatedByPipelineRunId { get; set; }
    public string? Changelog { get; set; }
    public int BuildNumber { get; set; }
    public string? CommitHash { get; set; }
    public string? TagName { get; set; }

    // F1: the candidate's sealed assurance verdict (CANDIDATE_ASSURANCE_GRADE / CANDIDATE_DEPLOYABLE /
    // CANDIDATE_BLOCKING_TESTS output variables published by the AssuranceSeal stage of the run that
    // published this release), hydrated once at publish time so the UI and the automatic deploy chain
    // do not need to re-parse step outputs. Null when the publishing run carries no such verdict (e.g.
    // a legacy release, or one published outside the candidate pipeline).
    public AnalysisGrade? AssuranceGrade { get; set; }
    public bool? Deployable { get; set; }
    public int? BlockingTestCount { get; set; }

    // Navigation
    public Project Project { get; set; } = null!;
    public PipelineRun? PipelineRun { get; set; }
    public PipelineRun? CreatedByPipelineRun { get; set; }

    // Cross-linking (many-to-many): a release can bundle several artifacts, span several commits,
    // and relate to several branches. See GitCommit / GitBranch.
    public List<PipelineArtifact> Artifacts { get; set; } = [];
    public List<GitCommit> Commits { get; set; } = [];
    public List<GitBranch> Branches { get; set; } = [];
    public List<ReleaseRollback> RollbacksFrom { get; set; } = [];
    public List<ReleaseRollback> RollbacksTo { get; set; } = [];
}
