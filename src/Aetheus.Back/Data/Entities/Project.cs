// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Data.Entities;

public class Project
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? RepositoryUrl { get; set; }
    public string? DefaultBranch { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;
    public string Tags { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // S-FEAT-15: per-project artifact retention overrides (days). Null falls back to the global
    // ArtifactStorage:DefaultRetentionDays / LatestRetentionDays config values.
    public int? ArtifactRetentionDays { get; set; }
    public int? ArtifactLatestRetentionDays { get; set; }

    // S-FEAT-16: default release version pattern used when a `type: release` step omits `version:`.
    // Null falls back to the built-in "1.0.$(BUILD_BUILDID)". Supports $(VAR) substitution.
    public string? ReleaseNumberingPattern { get; set; }

    /// <summary>
    /// Recette R2-110: how many release numbers this project has handed out, the last number of the
    /// application version (<c>major.minor.N</c>). A run of a pipeline declaring
    /// <c>release_number: allocate</c> (candidate, release-fast) takes the next one, atomically;
    /// every other run reads the latest. 0 until the first release, whose number is then 0.
    /// </summary>
    public int ReleaseCounter { get; set; }

    /// <summary>
    /// PLAN-013 lot 4 (recette R2-115): the « Conservation des données » profile; each nullable value
    /// below overrides one value of the profile (<see cref="DataRetentionProfiles.Resolve"/>).
    /// </summary>
    public DataRetentionProfile DataRetentionProfile { get; set; }
    public int? TelemetryExportIntervalMinutes { get; set; }
    public int? TelemetryDetailRetentionDays { get; set; }
    public int? TelemetryAggregateRetentionMonths { get; set; }
    public int? AnalysisDetailRetentionDays { get; set; }
    public int? AppLogRetentionDays { get; set; }

    /// <summary>
    /// PLAN-013 lot 5: the storage budget of the project in megabytes; null is the default (200 MB,
    /// <see cref="ProjectStorageDefaults.DefaultBudgetMegabytes"/>). Over it, the oldest detail goes first.
    /// </summary>
    public int? StorageBudgetMegabytes { get; set; }

    /// <summary>PLAN-013 lot 5: the last usage alert sent (0, 80 or 100 %), so each level is raised once
    /// until the usage falls back under 80 %.</summary>
    public int StorageQuotaAlertLevel { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    // External-Git parity (always on since recette R-295). Source discriminant: when set, this project's
    // repository is an external mirror-backed connection; otherwise the source is the internal
    // GitInternalRepo / RepositoryUrl. An external repository attached beside the internal one (recette
    // R-534) leaves this null: it is an additional source a pipeline names, not the project's source.
    public int? GitConnectionId { get; set; }
    public GitConnection? GitConnection { get; set; }

    // Navigation
    public List<Pipeline> Pipelines { get; set; } = [];
    public List<ProjectServer> ProjectServers { get; set; } = [];
}
