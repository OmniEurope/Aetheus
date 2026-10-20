// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Projects;

/// <summary>
/// One version series of a project (<c>major.minor</c>, the <c>VersionPrefix</c> of the code being
/// released) and the release number its next release takes. The number restarts at 0 in every series.
/// </summary>
public sealed record ProjectReleaseSeriesDto
{
    /// <summary><c>major.minor</c>, for example <c>1.4</c>.</summary>
    public string Series { get; init; } = string.Empty;

    /// <summary>The last number of the version the series' next release gets (<c>1.4.</c> + this).</summary>
    public int NextNumber { get; init; }
}

/// <summary>Sets the release number the next release of a series takes.</summary>
public sealed record SetNextReleaseNumberRequest
{
    /// <summary><c>major.minor</c>, for example <c>1.4</c>.</summary>
    [Required]
    [StringLength(ReleaseSeries.MaxLength)]
    [RegularExpression(ReleaseSeries.Pattern)]
    public string Series { get; init; } = string.Empty;

    /// <summary>The last number of the version the next release gets: 0 for <c>1.4.0</c>.</summary>
    [Range(0, ReleaseSeries.MaxNumber)]
    public int NextNumber { get; init; }
}
