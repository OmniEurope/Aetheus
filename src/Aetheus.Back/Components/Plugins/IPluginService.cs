// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Plugins;

public interface IPluginService
{
    Task<List<PluginRegistrationDto>> GetPluginsAsync(CancellationToken ct = default);
    /// <summary>The values the plugins list's checkable column filters offer.</summary>
    Task<PluginFilterValuesDto> GetFilterValuesAsync(CancellationToken ct = default);

    Task<PaginatedResult<PluginRegistrationDto>> GetPluginsPageAsync(
        PaginationRequest request, CancellationToken ct = default);
    Task<PluginRegistrationDto?> GetPluginAsync(int id, CancellationToken ct = default);
    Task<PluginRegistrationDto> RegisterPluginAsync(RegisterPluginRequest request, CancellationToken ct = default);
    Task<PluginRegistrationDto?> UpdatePluginAsync(int id, UpdatePluginRequest request, CancellationToken ct = default);
    Task<bool> UnregisterPluginAsync(int id, CancellationToken ct = default);
}
