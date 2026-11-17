// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// Moves the branch a pipeline's definition is read on (<c>PUT api/pipelines/{id}/source-branch</c>), from the planner's
/// selector (recette R-5, user decision 2026-11-09): the real source branch of the pipeline, stored, not a display choice.
/// Only <see cref="ScheduleDefaults.SourceBranchChoices"/> are accepted.
/// </summary>
public sealed record UpdatePipelineSourceBranchRequest
{
    /// <summary>The branch to read from now on, one of <see cref="ScheduleDefaults.SourceBranchChoices"/>.</summary>
    [Required]
    [StringLength(255)]
    public string Branch { get; init; } = string.Empty;

    /// <summary>The source branch the caller last read (null: the repository's default branch); a different stored value
    /// means the pipeline changed since and the write is refused (409).</summary>
    [StringLength(255)]
    public string? ExpectedBranch { get; init; }
}
