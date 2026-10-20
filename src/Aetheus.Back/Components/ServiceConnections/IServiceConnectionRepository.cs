// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.ServiceConnections;

public interface IServiceConnectionRepository
{
    Task<(List<ServiceConnection> Items, int TotalCount)> GetPagedAsync(
        string? search, int? projectId, int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = false,
        IReadOnlyList<GridFilter>? columnFilters = null);

    /// <summary>The distinct project names of the connections in scope.</summary>
    Task<List<string>> GetProjectNamesAsync(List<int>? accessibleIds, CancellationToken ct = default);
    Task<ServiceConnection?> GetDetailAsync(int id, CancellationToken ct = default);
    Task<ServiceConnection?> FindAsync(int id, CancellationToken ct = default);
    Task<List<ServiceConnection>> FindByNamesAsync(List<string> names, int? projectId, CancellationToken ct = default);
    Task AddAsync(ServiceConnection connection, CancellationToken ct = default);
    Task RemoveAsync(ServiceConnection connection, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
