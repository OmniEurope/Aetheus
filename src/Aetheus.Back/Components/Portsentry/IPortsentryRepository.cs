// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Portsentry;

public interface IPortsentryRepository
{
    Task<PortsentryState?> GetStateAsync(int serverId, CancellationToken ct = default);
    Task<List<PortsentryBlockedIp>> GetBlockedIpsAsync(int serverId, CancellationToken ct = default);
    Task<List<PortsentryWhitelistIp>> GetWhitelistAsync(int serverId, CancellationToken ct = default);
    Task<(List<PortsentryBlockedIp> Items, int Total)> GetBlockedIpsPagedAsync(
        int serverId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null);
    Task<(List<PortsentryWhitelistIp> Items, int Total)> GetWhitelistPagedAsync(
        int serverId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null);
    /// <summary>The distinct protocols of a server's blocked IPs, for the Protocol
    /// filter.</summary>
    Task<List<string>> GetBlockedProtocolsAsync(int serverId, CancellationToken ct = default);
    Task<PortsentryWhitelistIp> AddWhitelistIpAsync(PortsentryWhitelistIp entry, CancellationToken ct = default);
    Task<bool> RemoveWhitelistIpAsync(int id, int serverId, CancellationToken ct = default);
    Task AddTaskAsync(ServerTask task, CancellationToken ct = default);
}
