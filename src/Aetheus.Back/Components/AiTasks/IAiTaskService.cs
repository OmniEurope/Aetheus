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

    Task<PaginatedResult<AiTaskDefinitionDto>> GetDefinitionsAsync(
        PaginationRequest request, int? projectId, int? serverId,
        List<int>? accessibleProjectIds, List<int>? accessibleServerIds, CancellationToken ct);
    Task<AiTaskDefinitionDto?> GetDefinitionAsync(int id, CancellationToken ct);
    Task<AiTaskDefinitionDto> CreateDefinitionAsync(CreateAiTaskDefinitionRequest request, CancellationToken ct);
    Task<AiTaskDefinitionDto?> UpdateDefinitionAsync(
        int id, UpdateAiTaskDefinitionRequest request, CancellationToken ct);
    Task<bool> DeleteDefinitionAsync(int id, CancellationToken ct);
    Task<ServerTask> RunNowAsync(int definitionId, string? eventPayload, CancellationToken ct);
    /// <summary>Builds the run of a pipeline AI step with the profile <see cref="AiProfileResolver"/>
    /// picks: the step's named profile, else the pipeline's (<paramref name="pipelineProfileId"/>), else
    /// the organization's most preferred usable one. No spec when none can run; the problem says why:
    /// the pipeline decides what that costs (a failed run, or a failed advisory step).</summary>
    Task<AiPipelineExecution> BuildPipelineExecutionAsync(
        string? profileName, int? pipelineProfileId, int organizationId, string prompt, bool gate,
        int pipelineRunId, string workingDirectory, CancellationToken ct);

    /// <summary>Why no profile can run a pipeline AI step naming this profile (or none) in this
    /// organization, or null when one can. The pipeline's assigned profile is not an input: when it is
    /// usable the organization has a usable profile anyway, so it cannot change whether this is null.
    /// Read-only: the launch preflight asks it before a single task exists.</summary>
    Task<string?> FindPipelineProfileProblemAsync(string? profileName, int organizationId, CancellationToken ct);

    Task<PaginatedResult<AiRunResultDto>> GetResultsAsync(
        int? definitionId, int? pipelineRunId, PaginationRequest request, CancellationToken ct);
    Task<AiRunResultDto> PublishResultAsync(PublishAiRunResultRequest request, CancellationToken ct);
    Task<AiRunResultDto?> GetResultAsync(int id, CancellationToken ct);
    Task<AiConsumptionDto> GetConsumptionAsync(int? projectId, CancellationToken ct);
}

public sealed record AiExecutionSpec(
    string Target,
    int TimeoutSeconds,
    Dictionary<string, string> EnvironmentVariables);

/// <param name="Spec">The run to dispatch; null when no profile can run.</param>
/// <param name="Problem">Why no profile can run; null when <paramref name="Spec"/> is set.</param>
/// <param name="Resolution">Which profile was used and why (the same sentence the agent logs).</param>
/// <param name="IsFallback">The step's own profile name did not decide, so the pipeline records the choice
/// on the run as well.</param>
public sealed record AiPipelineExecution(
    AiExecutionSpec? Spec, string? Problem, string Resolution = "", bool IsFallback = false);
