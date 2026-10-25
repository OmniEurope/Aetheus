// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

public class GitInternalRepo
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DefaultBranch { get; set; } = "main";
    public bool IsEmpty { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastPushAt { get; set; }
    public int? GitConnectionId { get; set; }

    /// <summary>True for the repository that holds the project's system configuration files (Apache
    /// virtual hosts, environment sample, upstream); never the project's own source (ADR-057).</summary>
    public bool IsConfiguration { get; set; }

    // Navigation
    public Project Project { get; set; } = null!;
    public GitConnection? GitConnection { get; set; }
}
