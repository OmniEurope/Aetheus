// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.AgentPools;

public sealed record AgentPoolDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int MaxConcurrency { get; init; }
    /// <summary>The organization that owns the pool (R-12).</summary>
    public int OrganizationId { get; init; }
    public List<AgentPoolServerDto> Servers { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record AgentPoolServerDto
{
    public int ServerId { get; init; }
    public string ServerName { get; init; } = string.Empty;
    public ServerStatus ServerStatus { get; init; }
}

public abstract record AgentPoolMutationRequest
{
    [Required]
    [StringLength(100)]
    public string Name { get; init; } = string.Empty;

    [StringLength(500)]
    public string Description { get; init; } = string.Empty;

    [Range(1, 100)]
    public int MaxConcurrency { get; init; } = 1;

    [MaxLength(200)]
    public List<int> ServerIds { get; init; } = [];
}

public sealed record CreateAgentPoolRequest : AgentPoolMutationRequest
{
    /// <summary>The organization the pool goes to (R-12); without it, the one organization where the caller may
    /// create a pool, or the default organization for a site Admin.</summary>
    [Range(1, int.MaxValue)]
    public int? OrganizationId { get; init; }
}

public sealed record UpdateAgentPoolRequest : AgentPoolMutationRequest;
