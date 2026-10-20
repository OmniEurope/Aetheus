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

    /// <summary> How many release numbers this project has handed out, all series together. It is
    /// no longer a number source (the numbers count per series, see <see cref="ProjectReleaseSeries"/>):
    /// it stays because bumping it takes the project row lock that serializes two allocations of one
    /// project, and because the previous release of the backend still reads it. </summary>
    public int ReleaseCounter { get; set; }

    /// <summary>Selects the data retention profile; nullable values below override its settings
    /// through <see cref="DataRetentionProfiles.Resolve"/>.</summary>
    public DataRetentionProfile DataRetentionProfile { get; set; }
    public int? TelemetryExportIntervalMinutes { get; set; }
    public int? TelemetryDetailRetentionDays { get; set; }
    public int? TelemetryAggregateRetentionMonths { get; set; }
    public int? AnalysisDetailRetentionDays { get; set; }
    public int? AppLogRetentionDays { get; set; }

    /// <summary> The storage budget of the project in megabytes; null is the default (200 MB, <see
    /// cref="ProjectStorageDefaults.DefaultBudgetMegabytes"/>). Over it, the oldest detail goes
    /// first. </summary>
    public int? StorageBudgetMegabytes { get; set; }

    /// <summary>The last usage alert sent (0, 80 or 100 %), so each level is raised once until the
    /// usage falls back under 80 %.</summary>
    public int StorageQuotaAlertLevel { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    // When set, the project source is an external mirror-backed repository; otherwise it is
    // internal GitInternalRepo / RepositoryUrl. Additional external repositories referenced only by
    // pipelines leave this source discriminant null.
    public int? GitConnectionId { get; set; }
    public GitConnection? GitConnection { get; set; }

    // Navigation
    public List<Pipeline> Pipelines { get; set; } = [];
    public List<ProjectServer> ProjectServers { get; set; } = [];
}
