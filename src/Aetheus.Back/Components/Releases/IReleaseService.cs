// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Components.Releases;

public interface IReleaseService
{
    Task<PaginatedResult<ReleaseDto>> GetReleasesAsync(int? projectId, PaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default, bool deployableOnly = false);
    Task<ReleaseDto?> GetReleaseAsync(int id, CancellationToken ct = default);
    Task<ReleaseRollbackPreviewDto> GetRollbackPreviewAsync(int id, CancellationToken ct = default);
    Task<List<ReleaseDto>> GetReleasesByRunAsync(int pipelineRunId, CancellationToken ct = default);

    /// <summary>Releases of every project a server is involved in, enriched with their source
    /// pipeline like every other release view.</summary>
    Task<List<ReleaseDto>> GetServerReleasesAsync(int serverId, CancellationToken ct = default);

    /// <summary>One page of a server's releases. Preferred over the unpaged overload for any UI.</summary>
    Task<PaginatedResult<ReleaseDto>> GetServerReleasesAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default);

    /// <summary>Recette R-224: the project and source pipeline names across the releases of a scope
    /// (a server's when <paramref name="serverId"/> is set, else the accessible ones, of one project or all).</summary>
    Task<ReleaseFilterValuesDto> GetReleaseFilterValuesAsync(
        int? projectId, List<int>? accessibleIds, int? serverId, CancellationToken ct = default);
    Task<List<ReleaseDto>> SyncReleasesAsync(int projectId, CancellationToken ct = default);
    /// <param name="authorizedRun">The run of <c>request.PipelineId</c> the caller was authorized to launch
    /// (BACK-023); it is the one launched.</param>
    Task<ReleaseDto> TriggerReleaseBuildAsync(
        int releaseId, TriggerReleaseBuildRequest request, PipelineRunPreparation authorizedRun, CancellationToken ct = default);

    /// <param name="authorizedRun">The run of <c>request.PipelineId</c> the caller was authorized to launch
    /// (BACK-023); it is the one launched.</param>
    Task<ReleaseRollbackDto> RollbackReleaseAsync(
        int releaseId, RollbackReleaseRequest request, PipelineRunPreparation authorizedRun, CancellationToken ct = default);
    Task<ReleaseDto> PromoteReleaseAsync(int releaseId, CancellationToken ct = default);
    Task<ReleaseDto> CreateReleaseFromPipelineAsync(int projectId, int pipelineRunId, string version, string? changelog,
        string? commitHash = null, string? tagName = null, string? branchName = null,
        int? artifactPipelineRunId = null, bool deployed = false, CancellationToken ct = default);
    Task HandleWebhookAsync(WebhookPayload payload, CancellationToken ct = default);
    bool ValidateWebhookSignature(string? signature, string secret, string rawBody);
    Task NotifyPipelineRunCompletedAsync(int pipelineRunId, PipelineStatus status, CancellationToken ct = default);
    Task NotifyRollbackDeploymentSucceededAsync(int rollbackId, CancellationToken ct = default);
}
