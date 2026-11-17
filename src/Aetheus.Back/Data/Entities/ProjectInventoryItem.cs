// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>One package at one version in an inventory. <see cref="Ecosystem"/> and
/// <see cref="PackageName"/> use the spelling of <see cref="VulnerabilityAffectedPackage"/>, so an
/// advisory finds its items by index.</summary>
public class ProjectInventoryItem
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public int ProjectId { get; set; }
    public int ProjectInventoryId { get; set; }
    public string Ecosystem { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string PackageUrl { get; set; } = string.Empty;
    public bool IsDirect { get; set; }

    /// <summary>The first file that names this package at this version.</summary>
    public string? SourcePath { get; set; }

    public ProjectInventoryItemKind Kind { get; set; }

    public Organization Organization { get; set; } = null!;
    public Project Project { get; set; } = null!;
    public ProjectInventory ProjectInventory { get; set; } = null!;
}
