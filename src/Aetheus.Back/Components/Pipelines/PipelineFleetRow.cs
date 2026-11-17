// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

internal sealed record PipelineFleetRow(
    int PipelineId,
    string PipelineName,
    string Description,
    string YamlDefinition,
    string? SourceBranch,
    int? ProjectId,
    int? OwnerProjectId,
    int? EnvironmentId,
    int? ProjectServerId,
    string OwnerName,
    string OwnerType,
    int OrganizationId)
{
    /// <summary>Carried through a rewrite of the pipeline, which would otherwise return it to automatic.</summary>
    public int? AiRunnerProfileId { get; init; }

    /// <summary>Carried through a rewrite too: without it the update would read and write the definition in the
    /// project's default repository instead of the one the pipeline lives in, and forget that repository.</summary>
    public int? SourceRepositoryId { get; init; }
}
