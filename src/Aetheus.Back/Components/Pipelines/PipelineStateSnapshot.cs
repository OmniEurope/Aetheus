// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

internal sealed record PipelineStateSnapshot(
    string Name,
    string Description,
    string YamlDefinition,
    string? TemplateReferenceName,
    int? TemplateReferenceVersion,
    PipelineTriggerType TriggerType,
    int? ProjectId,
    int? SourceRepositoryId,
    string? SourceBranch,
    int? EnvironmentId,
    int? ProjectServerId,
    DateTime UpdatedAt,
    string? CreatedByUsername,
    int? AiRunnerProfileId)
{
    public static PipelineStateSnapshot Capture(Pipeline pipeline) => new(
        pipeline.Name, pipeline.Description, pipeline.YamlDefinition,
        pipeline.TemplateReferenceName, pipeline.TemplateReferenceVersion, pipeline.TriggerType,
        pipeline.ProjectId, pipeline.SourceRepositoryId, pipeline.SourceBranch, pipeline.EnvironmentId,
        pipeline.ProjectServerId, pipeline.UpdatedAt, pipeline.CreatedByUsername, pipeline.AiRunnerProfileId);

    public void Restore(Pipeline pipeline)
    {
        pipeline.Name = Name;
        pipeline.Description = Description;
        pipeline.YamlDefinition = YamlDefinition;
        pipeline.TemplateReferenceName = TemplateReferenceName;
        pipeline.TemplateReferenceVersion = TemplateReferenceVersion;
        pipeline.TriggerType = TriggerType;
        pipeline.ProjectId = ProjectId;
        pipeline.SourceRepositoryId = SourceRepositoryId;
        pipeline.SourceBranch = SourceBranch;
        pipeline.EnvironmentId = EnvironmentId;
        pipeline.ProjectServerId = ProjectServerId;
        pipeline.UpdatedAt = UpdatedAt;
        pipeline.CreatedByUsername = CreatedByUsername;
        pipeline.AiRunnerProfileId = AiRunnerProfileId;
    }
}
