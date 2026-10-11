// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AiTasks;

public interface IAiTaskService
{
    /// <summary>
    /// Starts whatever AI triggers are configured for a product event.
    ///
    /// Declared here rather than on a separate trigger port. That port existed so Notifications could
    /// call this without naming the module, but it resolved to this very service, so the dependency
    /// was real and the two sat in a cycle. Notifications now raises
    /// <c>NotificationEventRaisedEvent</c> and this module subscribes, which is the direction the
    /// concern actually runs: reacting to an event is the reactor's business.
    /// </summary>
    Task DispatchEventAsync(string eventType, object payload, CancellationToken ct);

    /// <summary>
    /// Pipeline behind a run, for the RBAC check on AI results filtered by run. An own-read: asking
    /// the orchestrator for a full run DTO to keep one integer put this module inside the cycle.
    /// Null means no such run, so the caller answers NotFound rather than Forbid.
    /// </summary>
    Task<int?> GetRunPipelineIdAsync(int pipelineRunId, CancellationToken ct);

    Task<PaginatedResult<AiRunnerProfileDto>> GetProfilesAsync(PaginationRequest request, CancellationToken ct);
    Task<List<AiRunnerProfileDto>> GetProfileOptionsAsync(List<int> organizationIds, CancellationToken ct);
    Task<AiRunnerProfileDto?> GetProfileAsync(int id, CancellationToken ct);
    Task<AiRunnerProfileDto> CreateProfileAsync(CreateAiRunnerProfileRequest request, CancellationToken ct);
    Task<AiRunnerProfileDto?> UpdateProfileAsync(int id, UpdateAiRunnerProfileRequest request, CancellationToken ct);
    Task<bool> DeleteProfileAsync(int id, CancellationToken ct);

    Task<PaginatedResult<AiTaskDefinitionDto>> GetDefinitionsAsync(
        PaginationRequest request, int? projectId, int? serverId,
        List<int>? accessibleProjectIds, List<int>? accessibleServerIds, CancellationToken ct);
    Task<AiTaskDefinitionDto?> GetDefinitionAsync(int id, CancellationToken ct);
    Task<AiTaskDefinitionDto> CreateDefinitionAsync(CreateAiTaskDefinitionRequest request, CancellationToken ct);
    Task<AiTaskDefinitionDto?> UpdateDefinitionAsync(
        int id, UpdateAiTaskDefinitionRequest request, CancellationToken ct);
    Task<bool> DeleteDefinitionAsync(int id, CancellationToken ct);
    Task<ServerTask> RunNowAsync(int definitionId, string? eventPayload, CancellationToken ct);
    /// <summary>Null when no profile of that name exists in the organization: the pipeline decides what
    /// that costs (a failed run, or a failed advisory step).</summary>
    Task<AiExecutionSpec?> BuildPipelineExecutionAsync(
        string profileName, int organizationId, string prompt, bool gate,
        int pipelineRunId, string workingDirectory, CancellationToken ct);

    Task<PaginatedResult<AiRunResultDto>> GetResultsAsync(
        int? definitionId, int? pipelineRunId, PaginationRequest request, CancellationToken ct);
    Task<AiRunResultDto> PublishResultAsync(PublishAiRunResultRequest request, CancellationToken ct);
    Task<AiRunResultDto?> GetResultAsync(int id, CancellationToken ct);
    Task<AiPatchApplicationDto> ApplyProposedPatchAsync(int resultId, CancellationToken ct);
    Task<AiConsumptionDto> GetConsumptionAsync(int? projectId, CancellationToken ct);
}

public sealed record AiExecutionSpec(
    string Target,
    int TimeoutSeconds,
    Dictionary<string, string> EnvironmentVariables);
