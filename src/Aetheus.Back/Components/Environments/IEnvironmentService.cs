// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Environments;

public interface IEnvironmentService
{
    Task<PaginatedResult<EnvironmentDto>> GetEnvironmentsAsync(int? projectId, PaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default);
    Task<EnvironmentDto?> GetEnvironmentAsync(int id, CancellationToken ct = default);
    Task<EnvironmentDto> CreateEnvironmentAsync(CreateEnvironmentRequest request, CancellationToken ct = default);
    Task<EnvironmentDto?> UpdateEnvironmentAsync(int id, UpdateEnvironmentRequest request, CancellationToken ct = default);
    Task<bool> DeleteEnvironmentAsync(int id, CancellationToken ct = default);
    Task<EnvironmentDto?> DuplicateEnvironmentAsync(int envId, int? targetProjectId, CancellationToken ct = default);
    Task<int?> GetProjectServerProjectIdAsync(int projectServerId, CancellationToken ct = default);

    /// <summary>What a copy of the environment would take from it: its servers and the projects of its
    /// linked project servers (RBAC audit, finding 2), or null when the environment does not exist.</summary>
    Task<EnvironmentCopyReferences?> GetCopyReferencesAsync(int id, CancellationToken ct = default);
    Task<bool> LinkProjectServerAsync(int envId, int projectServerId, CancellationToken ct = default);
    Task<bool> UnlinkProjectServerAsync(int envId, int projectServerId, CancellationToken ct = default);
}

/// <summary>The references a copy of an environment carries over: its servers and the projects owning its
/// linked project servers.</summary>
public sealed record EnvironmentCopyReferences(int? ProjectId, List<int> ServerIds, List<int> ProjectServerProjectIds);
