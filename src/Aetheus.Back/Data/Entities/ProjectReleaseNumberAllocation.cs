// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Data.Entities;

/// <summary>
/// A release number a series has handed out: written when a run takes it, kept after the run is
/// purged, so a version is never reused. The unique (project, series, number) key is what refuses a
/// second run, or an administrator, taking the same version.
/// </summary>
public class ProjectReleaseNumberAllocation
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Series { get; set; } = string.Empty;
    public int Number { get; set; }
    public DateTime AllocatedAt { get; set; }

    public Project Project { get; set; } = null!;
}
