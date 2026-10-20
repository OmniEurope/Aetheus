// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.ServerApps;

public interface IServerAppRepository
{
    Task<List<ServerApp>> GetByServerIdAsync(int serverId, CancellationToken ct = default);
    Task<(List<ServerApp> Items, int TotalCount)> GetPageAsync(
        int serverId, string? search, string? sortBy, bool sortDescending,
        int page, int pageSize, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? filters = null);
    /// <summary>The distinct sources of a server's applications, for the Source filter.</summary>
    Task<List<string>> GetSourcesAsync(int serverId, CancellationToken ct = default);
    Task<ServerApp?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<ServerApp> AddAsync(ServerApp app, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
    Task RemoveAsync(ServerApp app, CancellationToken ct = default);
}
