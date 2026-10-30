// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

public interface IServerRepository
{
    Task<(List<Server> Items, int TotalCount)> GetServersPagedAsync(
        string? search, string? sortBy, bool sortDescending,
        ServerType? type, ServerStatus? status,
        int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default);

    /// <summary>
    /// Projected variant of <see cref="GetServersPagedAsync"/> that returns <see cref="ServerDto"/>
    /// directly via a <c>.Select()</c> projection, avoiding loading full entity graphs.
    /// </summary>
    Task<(List<ServerDto> Items, int TotalCount)> GetServersPagedProjectedAsync(
        string? search, string? sortBy, bool sortDescending,
        ServerType? type, ServerStatus? status,
        int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default,
        IReadOnlyList<GridFilter>? columnFilters = null);

    /// <summary>The OS, agent version and tags of every server in scope, for the list's checkable
    /// column filters (their values, and the tag filter itself).</summary>
    Task<List<ServerFilterFact>> GetServerFilterFactsAsync(List<int>? accessibleIds, CancellationToken ct = default);

    Task<Server?> GetServerDetailAsync(int id, CancellationToken ct = default);
    // Heartbeat inventory writes, security counters and metric history moved to
    // IServerHeartbeatRepository: a caller that only reads servers has no business seeing fourteen
    // replace-the-world operations. Both share the caller's scoped AppDbContext, so one beat is
    // still one transaction.



    // Backward-compat for tests still calling the old method name.
    Task<Server?> GetServerWithCollectionsAsync(int id, CancellationToken ct = default);

    Task<Server?> FindServerAsync(int id, CancellationToken ct = default);

    /// <summary>Recette R3-24/R3-25: true when the server's mail stack carries Aetheus's managed marker.</summary>
    Task<bool> IsMailStackManagedAsync(int serverId, CancellationToken ct = default);

    /// <summary>
    /// Loads the server together with its tokens - used by the diagnostic
    /// endpoint to inspect token validity without re-issuing a second query.
    /// </summary>
    Task<Server?> FindServerWithTokensAsync(int id, CancellationToken ct = default);

    // Server removal uses IServerRetirementRepository to retire before purging.
















    Task SaveChangesAsync(CancellationToken ct = default);

    Task<bool> ServerExistsAsync(int serverId, CancellationToken ct = default);

    Task<List<Server>> GetStaleOnlineServersAsync(TimeSpan threshold, CancellationToken ct = default);

    /// <summary>
    /// Atomically changes an online server to offline only when its heartbeat still matches the
    /// timestamp observed by the timeout sweep and remains older than <paramref name="threshold"/>.
    /// Returns false when a concurrent heartbeat already refreshed the server.
    /// </summary>
    Task<bool> TryMarkOfflineIfStaleAsync(
        int serverId,
        DateTime observedLastHeartbeat,
        TimeSpan threshold,
        CancellationToken ct = default);

    Task<List<string>> GetServerNamesAsync(List<int>? accessibleIds = null, CancellationToken ct = default);

    Task<List<string>> GetServerNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Lightweight id/name projection for bulk operations (e.g. "update all agents").
    /// When <paramref name="accessibleIds"/> is non-null, the result is restricted to those ids.
    /// </summary>
    Task<List<(int Id, string Name)>> GetServerIdNamePairsAsync(List<int>? accessibleIds = null, CancellationToken ct = default);

    /// <summary>Loads the tracked server entities needed to reserve fleet-wide agent updates in one query.</summary>

    Task<List<int>> GetProjectIdsForServerAsync(int serverId, CancellationToken ct = default);

    Task<List<int>> GetPipelineIdsForServerAsync(int serverId, CancellationToken ct = default);

    Task<List<Project>> GetProjectsForServerAsync(int serverId, CancellationToken ct = default);
    Task<(List<Project> Items, int Total)> GetProjectsForServerPagedAsync(
        int serverId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null, List<int>? readableProjectIds = null);

    Task<List<Pipeline>> GetPipelinesForServerAsync(int serverId, CancellationToken ct = default);

    Task<List<VariableLibrary>> GetVariableLibrariesForServerAsync(int serverId, CancellationToken ct = default);

    Task<List<Vault>> GetVaultsForServerAsync(int serverId, CancellationToken ct = default);


    Task<ServiceType?> GetServiceTypeAsync(int serverId, string serviceName, CancellationToken ct = default);
    Task AddTaskAsync(ServerTask task, CancellationToken ct = default);
    Task AddTasksAsync(IEnumerable<ServerTask> tasks, CancellationToken ct = default);
    Task<(List<ServerTask> Items, int TotalCount)> GetTasksPagedAsync(int serverId, int page, int pageSize, CancellationToken ct = default);
    Task<int> CountActiveNonUpdateTasksAsync(int serverId, CancellationToken ct = default);
    Task<List<ServerDto>> GetServersForCompatibilityAsync(
        List<int>? accessibleIds,
        CancellationToken ct = default);

    /// <summary>Returns a filtered and sorted page of the server task log, newest first when no
    /// sort is supplied.</summary>
    Task<(List<TaskLog> Items, int TotalCount)> GetLogsPagedAsync(
        int serverId,
        int page,
        int pageSize,
        IReadOnlyList<GridFilter>? filters = null,
        IReadOnlyList<GridSort>? sorts = null,
        CancellationToken ct = default);
}
