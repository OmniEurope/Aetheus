// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Plugins;

public interface IPluginRepository
{
    Task<List<PluginRegistration>> GetAllAsync(CancellationToken ct = default);
    Task<(List<PluginRegistration> Items, int TotalCount)> GetPageAsync(
        string? search, string? sortBy, bool sortDescending,
        int page, int pageSize, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? columnFilters = null);

    /// <summary>The distinct authors of the registered plugins.</summary>
    Task<List<string>> GetAuthorsAsync(CancellationToken ct = default);
    Task<PluginRegistration?> FindAsync(int id, CancellationToken ct = default);
    Task<PluginRegistration?> FindByNameVersionAsync(string name, string version, CancellationToken ct = default);
    Task AddAsync(PluginRegistration plugin, CancellationToken ct = default);
    Task RemoveAsync(PluginRegistration plugin, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
