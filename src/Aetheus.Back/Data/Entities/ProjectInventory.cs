// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>
/// What one source says a project contains (PLAN-004). One row per project, origin and repository,
/// rewritten in place by each read. Reasons are stable codes, for the page to translate.
/// </summary>
public class ProjectInventory
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public int ProjectId { get; set; }

    /// <summary>Null for a project with no repository to read, and for an SBOM.</summary>
    public int? GitInternalRepoId { get; set; }

    public ProjectInventoryOrigin Origin { get; set; }
    public string? Ref { get; set; }
    public string? CommitSha { get; set; }
    public DateTime ReadAt { get; set; }

    /// <summary>When the set of items last changed: what the matching of lot 3 watches.</summary>
    public DateTime ChangedAt { get; set; }

    /// <summary>The <see cref="ChangedAt"/> these items were last matched at. Any other value means they are still to match.</summary>
    public DateTime? MatchedChangedAt { get; set; }

    public ProjectInventoryCoverage Coverage { get; set; }
    public string? Reason { get; set; }

    /// <summary>Identifies what was read (manifest blobs, or SBOM reports): the same value means nothing to parse.</summary>
    public string? SourceFingerprint { get; set; }

    public int ItemCount { get; set; }

    public Organization Organization { get; set; } = null!;
    public Project Project { get; set; } = null!;
    public GitInternalRepo? GitInternalRepo { get; set; }
    public List<ProjectInventoryItem> Items { get; set; } = [];
}
