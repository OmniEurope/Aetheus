// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.ServiceConnections;

public interface IServiceConnectionService
{
    Task<PaginatedResult<ServiceConnectionDto>> GetConnectionsAsync(int? projectId, PaginationRequest request, List<int>? accessibleIds = null, CancellationToken ct = default);

    /// <summary>The values the list's checkable column filters offer.</summary>
    Task<ServiceConnectionFilterValuesDto> GetFilterValuesAsync(List<int>? accessibleIds, CancellationToken ct = default);
    Task<ServiceConnectionDetailDto?> GetConnectionAsync(int id, CancellationToken ct = default);
    Task<ServiceConnectionDto> CreateConnectionAsync(CreateServiceConnectionRequest request, CancellationToken ct = default);
    Task<ServiceConnectionDto?> UpdateConnectionAsync(int id, UpdateServiceConnectionRequest request, CancellationToken ct = default);
    Task<bool> DeleteConnectionAsync(int id, CancellationToken ct = default);
    Task<ServiceConnectionTestResultDto?> TestConnectionAsync(int id, CancellationToken ct = default);
    Task<Dictionary<string, string>> ResolveConnectionSecretsAsync(List<string> names, int? projectId, CancellationToken ct = default);
}
