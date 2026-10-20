// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Pipelines;

public sealed record YamlValidationResultDto
{
    public bool IsValid { get; init; }
    public PipelineYamlDefinition? Definition { get; init; }
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed record ValidateYamlRequest
{
    [Required]
    [StringLength(PipelineDefinitionLimits.MaxYamlLength)]
    public string Yaml { get; init; } = string.Empty;

    public int? OrganizationId { get; init; }
}
