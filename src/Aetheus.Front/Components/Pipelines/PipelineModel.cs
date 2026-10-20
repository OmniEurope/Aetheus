// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

internal sealed class PipelineModel
{
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string Description { get; set; } = string.Empty;

    public string YamlDefinition { get; set; } = string.Empty;
    public int? ProjectId { get; set; }
    public string? SourceBranch { get; set; }
    public int? EnvironmentId { get; set; }
    public int? ProjectServerId { get; set; }

    /// <summary>The AI runner profile the pipeline's AI steps fall back on; null is automatic.</summary>
    public int? AiRunnerProfileId { get; set; }

    /// <summary>The form's data-annotation rules, for the save entry points outside the form (FRONT-008).</summary>
    public bool IsValid() =>
        Validator.TryValidateObject(this, new ValidationContext(this), validationResults: null, validateAllProperties: true);

    public CreatePipelineRequest ToCreateRequest() => new()
    {
        Name = Name,
        Description = Description,
        YamlDefinition = YamlDefinition,
        ProjectId = ProjectId,
        SourceBranch = SourceBranch,
        EnvironmentId = EnvironmentId,
        ProjectServerId = ProjectServerId,
        AiRunnerProfileId = AiRunnerProfileId
    };

    public UpdatePipelineRequest ToUpdateRequest() => new()
    {
        Name = Name,
        Description = Description,
        YamlDefinition = YamlDefinition,
        ProjectId = ProjectId,
        SourceBranch = SourceBranch,
        EnvironmentId = EnvironmentId,
        ProjectServerId = ProjectServerId,
        AiRunnerProfileId = AiRunnerProfileId
    };
}
