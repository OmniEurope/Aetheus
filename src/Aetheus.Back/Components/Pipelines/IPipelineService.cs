// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

public interface IPipelineService
{
    Task<PaginatedResult<PipelineDto>> GetPipelinesAsync(PipelinePaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default);
    Task<PipelineDto?> GetPipelineAsync(int id, CancellationToken ct = default);

    /// <summary>Project a project-server row belongs to, or null when it does not exist. Lets a caller
    /// authorize a pipeline owned by a project server, for which there is no resource type of its own.</summary>
    Task<int?> GetProjectServerProjectIdAsync(int projectServerId, CancellationToken ct = default);
    Task<PipelineSourceDto?> GetPipelineSourceAsync(int projectId, string pipelineName, CancellationToken ct = default,
        string? sourceBranch = null, int? sourceRepositoryId = null);

    /// <summary>The source of a pipeline from its four source columns. <c>Found</c> is false for an
    /// unknown pipeline; <c>Source</c> is null for one no project owns or with no
    /// repository.</summary>
    Task<(bool Found, PipelineSourceDto? Source)> GetPipelineSourceByIdAsync(int id, CancellationToken ct = default);
    Task<PipelineDependencyGroupsDto> GetDependencyGroupsAsync(
        List<int>? accessibleIds = null, int? serverId = null, int? projectId = null,
        CancellationToken ct = default);
    Task<PaginatedResult<PipelineDependencyDto>> GetDependencyPageAsync(PipelinePaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default);
    Task<PipelineDto> CreatePipelineAsync(CreatePipelineRequest request, CancellationToken ct = default);
    Task<PipelineDto?> UpdatePipelineAsync(int id, UpdatePipelineRequest request, CancellationToken ct = default);
    Task<bool> DeletePipelineAsync(int id, CancellationToken ct = default);
    PipelineYamlDefinition? ValidateYaml(string yaml);
    YamlValidationResultDto ValidateYamlStrict(string yaml);
    Task<YamlValidationResultDto> ValidateYamlStrictAsync(
        string yaml, int? projectId, int? environmentId, int? projectServerId,
        int? organizationId = null, CancellationToken ct = default);
    /// <summary>Returns webhook-triggered pipelines that belong to the given project.</summary>
    Task<List<PipelineDto>> GetWebhookTriggeredPipelinesForProjectAsync(int projectId, CancellationToken ct = default);

    /// <summary>Returns true if the pipeline has an active (running/pending) run.</summary>
    Task<bool> HasActiveRunAsync(int pipelineId, CancellationToken ct = default);

    /// <summary>Creates or updates a pipeline from YAML content for a given project.</summary>
    Task<PipelineDto> UpsertPipelineFromYamlAsync(
        string name, string yamlContent, int projectId, string triggerType,
        CancellationToken ct = default, string? sourceBranch = null, string? defaultBranch = null,
        int? sourceRepositoryId = null);
}
