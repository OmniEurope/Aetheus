// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations.Schema;

namespace Aetheus.Back.Data.Entities;

public class PipelineTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int OrganizationId { get; set; }
    public int LatestVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Organization Organization { get; set; } = null!;
    public List<PipelineTemplateVersion> Versions { get; set; } = [];

    // Compatibility facade for seed data and older callers. EF persists YAML only through Versions.
    [NotMapped]
    public string YamlContent
    {
        get => PipelineTemplateVersionFacade.LatestYaml(Versions);
        set => PipelineTemplateVersionFacade.SetYaml(Versions, LatestVersion, value);
    }

    [NotMapped]
    public int Version { get => LatestVersion; set => LatestVersion = value; }

    [NotMapped]
    public string Changelog
    {
        get => PipelineTemplateVersionFacade.Changelog(Versions);
        set { }
    }
}
