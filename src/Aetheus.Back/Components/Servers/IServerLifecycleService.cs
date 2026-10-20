// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// Server CRUD + read queries (lifecycle, listings, related-resource lookups).
/// Split out from <see cref="IServerService"/> so consumers can depend on the
/// minimal surface they actually need (Interface Segregation).
/// </summary>
public interface IServerLifecycleService
{
    Task<PaginatedResult<ServerDto>> GetServersAsync(PaginationRequest request, ServerType? type = null, ServerStatus? status = null, AgentCompatibilityStatus? compatibility = null, List<int>? accessibleIds = null, CancellationToken ct = default);
    /// <summary>Values of the servers list's checkable column filters.</summary>
    Task<ServerFilterValuesDto> GetServerFilterValuesAsync(List<int>? accessibleIds, CancellationToken ct = default);

    Task<AgentCompatibilitySummaryDto> GetAgentCompatibilitySummaryAsync(
        List<int>? accessibleIds = null,
        CancellationToken ct = default);
    Task<ServerDetailDto?> GetServerDetailAsync(int id, CancellationToken ct = default);
    Task<ServerDto?> UpdateServerAsync(int id, UpdateServerRequest request, CancellationToken ct = default);

    /// <summary>
    /// Flips the pipeline-runner capability for a server. Returns the updated DTO or null
    /// when the server doesn't exist. Caller is responsible for the RBAC check (Server.Admin).
    /// Idempotent: setting to the current value is a no-op (no audit entry).
    /// </summary>
    Task<ServerDto?> SetPipelineRunnerEnabledAsync(int id, bool enabled, string actorUsername, CancellationToken ct = default);

    /// <summary>Toggles the "containers only" policy on a runner. Server.Admin gated by the caller.
    /// Idempotent. When on, the scheduler refuses any non-container-isolated step on this server.</summary>
    Task<ServerDto?> SetContainerIsolationRequiredAsync(int id, bool required, string actorUsername, CancellationToken ct = default);

    // IServerRetirementService owns retirement and optional permanent purge.
    Task<bool> ServerExistsAsync(int serverId, CancellationToken ct = default);
    Task<List<string>> GetServerNamesAsync(CancellationToken ct = default);
    Task<List<string>> GetServerNamesAsync(List<int>? accessibleIds = null, CancellationToken ct = default);
    Task<PaginatedResult<ProjectDto>> GetServerProjectsAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default);
    Task<List<PipelineDto>> GetServerPipelinesAsync(int serverId, CancellationToken ct = default);
    Task<List<VariableLibraryDto>> GetServerVariableLibrariesAsync(int serverId, CancellationToken ct = default);
    Task<List<VaultDto>> GetServerVaultsAsync(int serverId, CancellationToken ct = default);
    Task<PaginatedResult<ServerTaskDto>> GetServerTasksAsync(int serverId, PaginationRequest request, CancellationToken ct = default);
    Task<PaginatedResult<TaskLogDto>> GetServerLogsAsync(int serverId, PaginationRequest request, CancellationToken ct = default);
}
