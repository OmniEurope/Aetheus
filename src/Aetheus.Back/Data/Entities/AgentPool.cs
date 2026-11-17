// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

public class AgentPool
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int MaxConcurrency { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>The organization that owns the pool (R-12): only its members reach it, and it holds only
    /// that organization's servers.</summary>
    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    // Navigation
    public List<AgentPoolServer> Servers { get; set; } = [];
}
