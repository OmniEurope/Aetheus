// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Projects;

public sealed record ProjectDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? RepositoryUrl { get; init; }
    public int? InternalRepositoryId { get; init; }
    public string? DefaultBranch { get; init; }
    public ProjectStatus Status { get; init; }

    /// <summary>Recette R-51 (ADR-058): set while the project is deleted but something it deployed is still on a
    /// server (« Suppression à terminer »): unusable until its cleanup is relaunched to the end.</summary>
    public DateTime? DeletionRequestedAt { get; init; }

    public List<string> Tags { get; init; } = [];
    public int PipelineCount { get; init; }
    public int OrganizationId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    /// <summary>Status of the selected root pipeline run, excluding runs triggered by another run
    /// step; null when the project has never run.</summary>
    public PipelineStatus? LastRunStatus { get; init; }
    public DateTime? LastRunAt { get; init; }
    public int? LastRunId { get; init; }
    /// <summary>R-8: that run's number within its pipeline.</summary>
    public int? LastRunNumber { get; init; }
    public string? LastRunName { get; init; }

    /// <summary>Indicates the selected root run is still Running, taking precedence over a newer
    /// completed run.</summary>
    public bool LastRunIsActive { get; init; }

    /// <summary>The step the active run is on, as "stage · step"; null when the run is not active.</summary>
    public string? LastRunCurrentStep { get; init; }

    /// <summary>Current project-wide A-F grade (R-12, R-20): the sealed assurance verdict of the release deployed
    /// in production, F when its seal refused it. Null when nothing is deployed or the deployed release carries no
    /// verdict; <see cref="LatestGateGradeQualification"/> says which.</summary>
    public AnalysisGrade? LatestGateGrade { get; init; }

    /// <summary>Where <see cref="LatestGateGrade"/> comes from. Null when the project has no analysis grade and
    /// no sealed verdict.</summary>
    public AnalysisQualificationDto? LatestGateGradeQualification { get; init; }

    /// <summary>Most recent git activity for the project: the latest of the internal repository's
    /// last push and any external connection's last sync. Null when the project has no git.
    /// Drives the "last git update" chip on the projects list.</summary>
    public DateTime? LastGitUpdateAt { get; init; }

    /// <summary>Most recent first-class commit observed for the project. The id deep-links to the
    /// commit detail while the SHA/message/date keep the project tile useful without another call.</summary>
    public int? LastCommitId { get; init; }
    public string? LastCommitSha { get; init; }
    public string? LastCommitMessage { get; init; }
    public DateTime? LastCommitAt { get; init; }

    /// <summary>Availability aggregated from enabled production monitored apps. Unavailable means
    /// no production monitor exists or its current state is unknown.</summary>
    public ProjectProductionStatus ProductionStatus { get; init; } = ProjectProductionStatus.Unavailable;

    /// <summary>Active analytics sessions seen in the last five minutes across production apps.
    /// Null means web analytics is not available; zero is a measured absence of active sessions only when
    /// <see cref="OnlineUserState"/> is <see cref="OnlineAudienceState.Measured"/>.</summary>
    public int? OnlineUserCount { get; init; }

    /// <summary>R-13/R-23: whether <see cref="OnlineUserCount"/> was measured, is waiting for data (nothing
    /// ingested in the window), or is unavailable (web analytics not enabled).</summary>
    public OnlineAudienceState OnlineUserState { get; init; } = OnlineAudienceState.Unavailable;

    /// <summary>R-23: when web analytics last ingested an event for the production apps (UTC); null when it never did.
    /// Told next to a <see cref="OnlineAudienceState.Waiting"/> counter so the wait has a starting point.</summary>
    public DateTime? OnlineUserLastEventAt { get; init; }
}

public sealed record ProjectDetailDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? RepositoryUrl { get; init; }
    public string? DefaultBranch { get; init; }
    public ProjectStatus Status { get; init; }

    /// <summary>Recette R-51 (ADR-058): set while the project is deleted but something it deployed is still on a
    /// server (« Suppression à terminer »): unusable until its cleanup is relaunched to the end.</summary>
    public DateTime? DeletionRequestedAt { get; init; }

    public List<string> Tags { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    /// <summary>Current project-wide A-F grade (R-12, R-20): the sealed assurance verdict of the release deployed
    /// in production, F when its seal refused it. Null when nothing is deployed or the deployed release carries no
    /// verdict; <see cref="LatestGateGradeQualification"/> says which.</summary>
    public AnalysisGrade? LatestGateGrade { get; init; }

    /// <summary>Where <see cref="LatestGateGrade"/> comes from (deployed release, state). Null when the project
    /// has no analysis grade and no sealed verdict.</summary>
    public AnalysisQualificationDto? LatestGateGradeQualification { get; init; }

    /// <summary>The run whose seal published the deployed release's verdict (<see cref="LatestGateGrade"/>), to
    /// open on its Gate tab.</summary>
    public int? LatestGateGradeRunId { get; init; }
    /// <summary>R-8: that run's number within its pipeline.</summary>
    public int? LatestGateGradeRunNumber { get; init; }

    /// <summary>What lowers <see cref="LatestGateGrade"/> below A (the pipeline run of its seal, or the project's own
    /// open findings); null when the grade is A or has no letter.</summary>
    public AnalysisGradeCause? LatestGateGradeCause { get; init; }

    /// <summary>The pipeline of <see cref="LatestGateGradeRunId"/>, named when <see cref="LatestGateGradeCause"/> is the pipeline.</summary>
    public int? LatestGateGradePipelineId { get; init; }
    [StringLength(200)] public string? LatestGateGradePipelineName { get; init; }

    /// <summary>Most recent git activity (internal last push / external last sync); null when the
    /// project has no git. Shown in the overview "Repository" card.</summary>
    public DateTime? LastGitUpdateAt { get; init; }

    /// <summary>R-29: the address (application, and API when it answers elsewhere) deployed by the project's latest successful
    /// production deployment run. Null when no run deployed to production: nothing is shown then.</summary>
    public PipelineRunDeployedAddressDto? ProductionAddress { get; init; }

    public List<PipelineDto> Pipelines { get; init; } = [];

    // S-FEAT-15 / S-FEAT-16: per-project overrides (null = use the global / built-in default).
    public int? ArtifactRetentionDays { get; init; }
    public int? ArtifactLatestRetentionDays { get; init; }
    public string? ReleaseNumberingPattern { get; init; }

    /// <summary>R-13: the project follows the expand/contract model for its database (its migration gate is on): the overview
    /// shows the expand/contract tracking tab even before an environment and a database are declared.</summary>
    public bool MigrationGateEnabled { get; init; }

    // S-TECH-N8R3: per-section counts so the project detail can render enriched section tiles
    // (Servers / Releases / Vaults / Libraries / Environments) without N extra round-trips.
    public int ServerCount { get; init; }

    /// <summary>Counts distinct environment-linked servers separately from <see
    /// cref="ServerCount"/>, which represents the Servers section rows. Either count can satisfy
    /// onboarding.</summary>
    public int EnvironmentServerCount { get; init; }

    public int ReleaseCount { get; init; }
    public int VaultCount { get; init; }
    public int LibraryCount { get; init; }
    public int EnvironmentCount { get; init; }
}

public abstract record ProjectRequest
{
    [Required]
    [StringLength(100)]
    public string Name { get; init; } = string.Empty;

    [StringLength(500)]
    public string Description { get; init; } = string.Empty;

    [StringLength(500)]
    [HttpsUrl]
    public string? RepositoryUrl { get; init; }

    [StringLength(100)]
    public string? DefaultBranch { get; init; }

    [MaxLength(20)]
    [MaxItemStringLength(50)]
    public List<string> Tags { get; init; } = [];

    [Range(1, 3650)]
    public int? ArtifactRetentionDays { get; init; }

    [Range(1, 3650)]
    public int? ArtifactLatestRetentionDays { get; init; }

    [StringLength(100)]
    public string? ReleaseNumberingPattern { get; init; }

}

public sealed record CreateProjectRequest : ProjectRequest
{
    /// <summary>Organization that owns the new project. Optional -
    /// when omitted, the API uses the caller's default organization.</summary>
    [Range(1, int.MaxValue)]
    public int? OrganizationId { get; init; }
}

public sealed record UpdateProjectRequest : ProjectRequest
{
    public ProjectStatus Status { get; init; }
}

/// <summary>R-31: the branch the pipeline setup wizard chose, written to the project's default branch beside the
/// <c>source_branch:</c> of the pipelines it created.</summary>
public sealed record SetProjectDefaultBranchRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Branch { get; init; } = string.Empty;
}

/// <summary>R-13: turns the project's expand/contract model (its migration gate) on or off, from the pipeline setup wizard or the
/// project's settings.</summary>
public sealed record SetProjectMigrationGateRequest
{
    public bool Enabled { get; init; }
}

public sealed record ProjectActivityDto
{
    public string Type { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Status { get; init; }
    public DateTime Timestamp { get; init; }
    public string? Icon { get; init; }
}
