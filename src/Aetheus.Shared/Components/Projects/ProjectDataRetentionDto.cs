// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Projects;

/// <summary>A project's « Conservation des données » setting and the values it applies.</summary>
public sealed record ProjectDataRetentionDto
{
    public int ProjectId { get; init; }
    public DataRetentionProfile Profile { get; init; }
    public DataRetentionOverrides Overrides { get; init; } = DataRetentionOverrides.None;
    public DataRetentionValues Effective { get; init; } = DataRetentionProfiles.Standard;

    /// <summary>The storage budget in megabytes; null is the default (200).</summary>
    public int? StorageBudgetMegabytes { get; init; }
}

/// <summary>The profile and the overrides to save; a null override keeps the profile's
/// value.</summary>
public sealed record UpdateProjectDataRetentionRequest
{
    [EnumDataType(typeof(DataRetentionProfile))]
    public DataRetentionProfile Profile { get; init; }

    [Required]
    public DataRetentionOverrides Overrides { get; init; } = DataRetentionOverrides.None;

    /// <summary>The storage budget in megabytes; null keeps the default (200).</summary>
    [Range(ProjectStorageDefaults.MinimumBudgetMegabytes, ProjectStorageDefaults.MaximumBudgetMegabytes)]
    public int? StorageBudgetMegabytes { get; init; }
}
