// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

/// <summary>Persists server retirement while preserving links; permanent purge is restricted to
/// already retired servers.</summary>
public interface IServerRetirementRepository
{
    /// <summary>Loads a server for update whether it is active or retired (null when it never existed
    /// or was purged).</summary>
    Task<Server?> FindServerIncludingRetiredAsync(int id, CancellationToken ct = default);

    /// <summary>Marks the tracked <paramref name="server"/> retired at <paramref name="retiredAt"/>,
    /// offline, and revokes every agent token it holds, in one save.</summary>
    Task RetireAsync(Server server, DateTime retiredAt, CancellationToken ct = default);

    Task<(List<RetiredServerDto> Items, int TotalCount)> GetRetiredServersPagedAsync(
        string? search, string? sortBy, bool sortDescending, int page, int pageSize, List<int>? accessibleIds,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null);

    /// <summary>Hard-deletes the server and everything hanging off it, including its backup policies
    /// (their FK is Restrict, so they would otherwise block the delete). Returns how many backup
    /// policies were deleted with it.</summary>
    Task<int> PurgeServerAsync(Server server, CancellationToken ct = default);
}
