// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Pipelines;

public enum PipelineFleetFreshness
{
    Current,
    Outdated,
    OffCatalog
}

public sealed record PipelineFleetPaginationRequest : PaginationRequest
{
    public int? TemplateId { get; init; }
    public int? ProjectId { get; init; }
    public PipelineFleetFreshness? Freshness { get; init; }
}

public sealed record PipelineFleetItemDto
{
    public int PipelineId { get; init; }
    public string PipelineName { get; init; } = string.Empty;
    public int? ProjectId { get; init; }
    public string OwnerName { get; init; } = string.Empty;
    public string OwnerType { get; init; } = string.Empty;
    public int OrganizationId { get; init; }
    public int? TemplateId { get; init; }
    public string? TemplateName { get; init; }
    public int? PinnedVersion { get; init; }
    public int? LatestVersion { get; init; }
    public bool UsesLegacyReference { get; init; }
    public PipelineFleetFreshness Freshness { get; init; }
}

public sealed record PipelineFleetUpdateRequest
{
    [Range(1, int.MaxValue)]
    public int TargetVersion { get; init; }
    public bool AcknowledgeOrphanOverrides { get; init; }
    [StringLength(64, MinimumLength = 64)]
    public string? ExpectedSourceYamlHash { get; init; }
}

public sealed record PipelineFleetUpdatePreviewDto
{
    public int PipelineId { get; init; }
    public string TemplateName { get; init; } = string.Empty;
    public int CurrentVersion { get; init; }
    public int TargetVersion { get; init; }
    public string CurrentResolvedYaml { get; init; } = string.Empty;
    public string TargetResolvedYaml { get; init; } = string.Empty;
    public List<string> OrphanOverrides { get; init; } = [];
    public string SourceYamlHash { get; init; } = string.Empty;
    public bool RequiresOrphanAcknowledgement => OrphanOverrides.Count > 0;
}

public sealed record ExtractPipelineTemplateRequest
{
    [Required]
    [StringLength(100)]
    public string TemplateName { get; init; } = string.Empty;
    [StringLength(500)]
    public string Description { get; init; } = string.Empty;
    [Required]
    [StringLength(50)]
    public string Category { get; init; } = "Pipeline";
    [StringLength(50_000)]
    public string? TemplateYamlContent { get; init; }
    [StringLength(50_000)]
    public string? RewrittenPipelineYaml { get; init; }
    public bool RewritePipeline { get; init; } = true;
}

public sealed record PromotePipelineTemplateRequest
{
    [Required]
    [StringLength(500)]
    public string ChangelogEntry { get; init; } = string.Empty;
    [Required]
    [StringLength(50_000)]
    public string YamlContent { get; init; } = string.Empty;
    public bool RebaseSourcePipeline { get; init; }
}

public sealed record PipelinePromotePreviewDto
{
    public int PipelineId { get; init; }
    public int TemplateId { get; init; }
    public string TemplateName { get; init; } = string.Empty;
    public int LatestVersion { get; init; }
    public string LatestTemplateYaml { get; init; } = string.Empty;
    public string EffectivePipelineYaml { get; init; } = string.Empty;
}

/// <summary>The template names the fleet's Template column filter offers, read across every
/// pipeline the caller can read.</summary>
public sealed record PipelineFleetFilterValuesDto
{
    public List<string> Templates { get; init; } = [];
}
