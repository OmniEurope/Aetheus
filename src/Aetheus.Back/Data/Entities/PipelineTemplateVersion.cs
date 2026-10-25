// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>An immutable published version of an organization-scoped pipeline template.</summary>
public sealed class PipelineTemplateVersion : IPipelineTemplateVersionContent
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public int Version { get; set; }
    public string YamlContent { get; set; } = string.Empty;
    public string ChangelogEntry { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string CreatedByUsername { get; set; } = string.Empty;

    public PipelineTemplate Template { get; set; } = null!;
}
